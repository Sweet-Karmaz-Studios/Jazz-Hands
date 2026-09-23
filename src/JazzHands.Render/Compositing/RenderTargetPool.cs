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

        if (_idle.TryGetValue(key, out Stack<RenderTarget>? spare) && spare.Count > 0)
        {
            return spare.Pop();
        }

        Created++;
        return new RenderTarget(_device, width, height, format);
    }

    /// <summary>Gives a target back.</summary>
    public void Return(RenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
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
