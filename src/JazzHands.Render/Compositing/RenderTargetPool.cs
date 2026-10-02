using Serilog;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JazzHands.Render.Compositing;

/// <summary>A texture the GPU can draw into and read from, with both views made once.</summary>
public sealed class RenderTarget : IDisposable
{
    internal RenderTarget(RenderDevice device, int width, int height, Format format)
    {
        Width = width;
        Height = height;
        Format = format;
        Texture = device.CreateRenderTarget(width, height, format);
        View = device.Device.CreateRenderTargetView(Texture);
        Resource = device.Device.CreateShaderResourceView(Texture);
    }

    /// <summary>The texture.</summary>
    public ID3D11Texture2D Texture { get; }

    /// <summary>For drawing into it.</summary>
    public ID3D11RenderTargetView View { get; }

    /// <summary>For sampling from it.</summary>
    public ID3D11ShaderResourceView Resource { get; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The pixel format.</summary>
    public Format Format { get; }

    /// <summary>Roughly what it costs in video memory.</summary>
    public long Bytes => (long)Width * Height * BytesPerPixel(Format);

    /// <summary>True while it waits in its pool, between a return and the next rent.</summary>
    internal bool Idle { get; set; }

    /// <inheritdoc />
    public void Dispose()
    {
        Resource.Dispose();
        View.Dispose();
        Texture.Dispose();
    }

    private static int BytesPerPixel(Format format) => format switch
    {
        Format.R16G16B16A16_Float => 8,
        Format.R32G32B32A32_Float => 16,
        _ => 4,
    };
}

/// <summary>
/// Render targets by size and format, handed back and forth so steady-state compositing creates
/// none.
/// </summary>
/// <remarks>
/// Every pass rents what it writes into and gives back what it has finished reading. A timeline of
/// one format at one quality settles after its first frame into a fixed set that goes round and
/// round, which is the exit criterion's "no textures in steady state"; <see cref="Created"/> is how
/// a test and the perf panel tell. A change of quality or sequence size is a new set, and the old
/// one is dropped by <see cref="Trim"/> once it has not been asked for in a while.
///
/// Thread affine to the device context, like the passes that use it.
/// </remarks>
public sealed class RenderTargetPool : IDisposable
{
    private readonly ILogger _log = Log.ForContext<RenderTargetPool>();
    private readonly RenderDevice _device;
    private readonly Dictionary<(int Width, int Height, Format Format), Stack<RenderTarget>> _idle = [];
    private readonly Dictionary<(int Width, int Height, Format Format), long> _lastUsed = [];
    private long _clock;
    private bool _disposed;

    /// <summary>
    /// Throws on a target given back twice rather than logging it and keeping one: the test
    /// suites turn it on with the <c>JazzHands.Render.StrictPools</c> switch, so the second owner
    /// is found by its stack (Phase 33). A target in a pool twice is handed to two renders at
    /// once, and one draws over the other's picture.
    /// </summary>
    public static bool ThrowOnDoubleReturn { get; set; } = AppContext.TryGetSwitch("JazzHands.Render.StrictPools", out bool strict) && strict;

    /// <summary>
    /// Fills every target handed out with values no picture has (not-a-number in a float target,
    /// which comes out black and spreads through anything blended with it; magenta in the rest),
    /// so a pass that reads what it never wrote shows every time rather than when the memory
    /// behind a new texture happens to hold something. On with the same switch, for the tests.
    /// </summary>
    public static bool PoisonOnRent { get; set; } = AppContext.TryGetSwitch("JazzHands.Render.StrictPools", out bool poison) && poison;

    /// <summary>Targets given back while they were already in the pool; always zero when all is well.</summary>
    public long DoubleReturns { get; private set; }

    /// <summary>Creates a pool over a device.</summary>
    public RenderTargetPool(RenderDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    /// <summary>Targets ever created. Flat once compositing reaches a steady state.</summary>
    public long Created { get; private set; }

    /// <summary>Targets handed out.</summary>
    public long Rented { get; private set; }

    /// <summary>Targets out on loan right now.</summary>
    public int Outstanding { get; private set; }

    /// <summary>Targets waiting to be rented.</summary>
    public int Idle => _idle.Values.Sum(stack => stack.Count);

    /// <summary>The targets waiting to be rented, for tests of what still holds on to them.</summary>
    internal IEnumerable<RenderTarget> IdleTargets => _idle.Values.SelectMany(stack => stack);

    /// <summary>What the idle targets cost in video memory.</summary>
    public long IdleBytes => _idle.Values.Sum(stack => stack.Sum(target => target.Bytes));

    /// <summary>A target of a size and format, recycled when one is free.</summary>
    public RenderTarget Rent(int width, int height, Format format = Format.R16G16B16A16_Float)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var key = (width, height, format);
        _lastUsed[key] = ++_clock;
        Rented++;
        Outstanding++;

        RenderTarget target;
        if (_idle.TryGetValue(key, out Stack<RenderTarget>? spare) && spare.Count > 0)
        {
            target = spare.Pop();
            target.Idle = false;
        }
        else
        {
            Created++;
            target = new RenderTarget(_device, width, height, format);
        }

        if (PoisonOnRent)
        {
            bool floats = format is Format.R16G16B16A16_Float or Format.R32G32B32A32_Float or Format.R16_Float or Format.R32_Float or Format.R16G16_Float or Format.R32G32_Float;
            _device.ImmediateContext.ClearRenderTargetView(target.View, floats ? new Vortice.Mathematics.Color4(float.NaN, float.NaN, float.NaN, float.NaN) : new Vortice.Mathematics.Color4(1.0f, 0.0f, 1.0f, 1.0f));
        }

        return target;
    }

    /// <summary>Gives a target back.</summary>
    public void Return(RenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Idle)
        {
            // Kept once: in the pool twice it would be rented to two renders at the same time.
            DoubleReturns++;
            _log.Error("A {Width}x{Height} render target was given back twice; the second is ignored. {Stack}", target.Width, target.Height, Environment.StackTrace);
            if (ThrowOnDoubleReturn)
            {
                throw new InvalidOperationException($"A {target.Width}x{target.Height} render target was given back to its pool twice.");
            }

            return;
        }

        Outstanding--;

        if (_disposed)
        {
            target.Dispose();
            return;
        }

        var key = (target.Width, target.Height, target.Format);
        if (!_idle.TryGetValue(key, out Stack<RenderTarget>? spare))
        {
            spare = new Stack<RenderTarget>();
            _idle[key] = spare;
        }

        target.Idle = true;
        spare.Push(target);
    }

    /// <summary>
    /// Releases idle targets of sizes not asked for in the last <paramref name="rents"/> rents,
    /// which is what a change of quality or sequence leaves behind.
    /// </summary>
    public void Trim(long rents = 256)
    {
        foreach (((int, int, Format) key, Stack<RenderTarget> spare) in _idle)
        {
            if (_clock - _lastUsed.GetValueOrDefault(key) <= rents)
            {
                continue;
            }

            while (spare.Count > 0)
            {
                spare.Pop().Dispose();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (Stack<RenderTarget> spare in _idle.Values)
        {
            while (spare.Count > 0)
            {
                spare.Pop().Dispose();
            }
        }

        _idle.Clear();
        _log.Debug("Render target pool closed after creating {Created} and renting {Rented}", Created, Rented);
    }
}
