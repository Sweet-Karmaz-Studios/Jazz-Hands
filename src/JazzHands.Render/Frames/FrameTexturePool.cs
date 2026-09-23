using Vortice.Direct3D11;
using Serilog;

namespace JazzHands.Render.Frames;

/// <summary>
/// Recycles frame textures, because creating one is a driver allocation and playback wants sixty
/// a second.
/// </summary>
/// <remarks>
/// Keyed by what makes two textures interchangeable: layout, width and height. A timeline of one
/// format and one size, which is most of them, reaches a steady state where nothing is allocated
/// at all. Mixed sizes keep a small set per size and the least useful are dropped when the pool
/// is trimmed.
///
/// Thread affine to the device context that creates the textures, like everything else touching
/// Direct3D here.
/// </remarks>
public sealed class FrameTexturePool : IDisposable
{
    private readonly ILogger _log = Log.ForContext<FrameTexturePool>();
    private readonly RenderDevice _device;
    private readonly Dictionary<Key, Stack<ID3D11Texture2D[]>> _idle = [];
    private readonly int _perSize;
    private bool _disposed;

    /// <summary>Creates a pool over a device.</summary>
    /// <param name="device">The device textures are created on. Not owned.</param>
    /// <param name="idlePerSize">How many spare sets to keep for each layout and size.</param>
    public FrameTexturePool(RenderDevice device, int idlePerSize = 8)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegative(idlePerSize);

        _device = device;
        _perSize = idlePerSize;
    }

    /// <summary>Texture sets ever created. Flat in steady state is the point.</summary>
    public long Created { get; private set; }

    /// <summary>Texture sets handed out from the idle list rather than created.</summary>
    public long Recycled { get; private set; }

    /// <summary>Texture sets currently held for reuse.</summary>
    public int Idle => _idle.Values.Sum(stack => stack.Count);

    /// <summary>
    /// A frame texture of a layout and size, recycled when one is spare.
    /// </summary>
    /// <param name="usage">
    /// What the caller will do with it. A copy target needs nothing special; an upload target has
    /// to be writable from the CPU.
    /// </param>
    public FrameTexture Rent(PixelLayout layout, int width, int height, FrameTextureUsage usage)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = new Key(layout.Name, width, height, usage);

        if (_idle.TryGetValue(key, out Stack<ID3D11Texture2D[]>? spare) && spare.Count > 0)
        {
            Recycled++;
            return new FrameTexture(spare.Pop(), layout, width, height, this);
        }

        var planes = new ID3D11Texture2D[layout.PlaneCount];

        try
        {
            for (int index = 0; index < planes.Length; index++)
            {
                PixelPlane plane = layout.Planes[index];
                planes[index] = Create(plane, plane.WidthFor(width), plane.HeightFor(height), usage);
            }
        }
        catch
        {
            foreach (ID3D11Texture2D? made in planes)
            {
                made?.Dispose();
            }

            throw;
        }

        Created++;
        return new FrameTexture(planes, layout, width, height, this);
    }

    /// <summary>Drops every spare texture, which is what a memory pinch or a device change needs.</summary>
    public void Trim()
    {
        foreach (Stack<ID3D11Texture2D[]> spare in _idle.Values)
        {
            while (spare.Count > 0)
            {
                foreach (ID3D11Texture2D plane in spare.Pop())
                {
                    plane.Dispose();
                }
            }
        }

        _idle.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Trim();

        _log.Debug("Frame texture pool closed after creating {Created} and recycling {Recycled}", Created, Recycled);
    }

    /// <summary>Takes a set back. Called by the frame texture, never directly.</summary>
    internal void Return(FrameTexture frame, ID3D11Texture2D[] planes)
    {
        var key = new Key(frame.Layout.Name, frame.Width, frame.Height, UsageOf(planes[0]));

        if (_disposed || !_idle.TryGetValue(key, out Stack<ID3D11Texture2D[]>? spare))
        {
            if (_disposed)
            {
                foreach (ID3D11Texture2D plane in planes)
                {
                    plane.Dispose();
                }

                return;
            }

            spare = new Stack<ID3D11Texture2D[]>();
            _idle[key] = spare;
        }

        if (spare.Count >= _perSize)
        {
            foreach (ID3D11Texture2D plane in planes)
            {
                plane.Dispose();
            }

            return;
        }

        spare.Push(planes);
    }

    private static FrameTextureUsage UsageOf(ID3D11Texture2D texture) =>
        texture.Description.Usage == ResourceUsage.Dynamic
            ? FrameTextureUsage.Upload
            : FrameTextureUsage.Copy;

    private ID3D11Texture2D Create(PixelPlane plane, int width, int height, FrameTextureUsage usage)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = plane.Format,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = usage == FrameTextureUsage.Upload ? CpuAccessFlags.Write : CpuAccessFlags.None,

            // Dynamic for an upload target so a map with discard does not stall on the last use;
            // default for a copy target because the GPU writes it and only the GPU reads it.
            Usage = usage == FrameTextureUsage.Upload ? ResourceUsage.Dynamic : ResourceUsage.Default,
        };

        return _device.Device.CreateTexture2D(description);
    }

    private readonly record struct Key(string Layout, int Width, int Height, FrameTextureUsage Usage);
}

/// <summary>What a frame texture is going to be filled by.</summary>
public enum FrameTextureUsage
{
    /// <summary>The GPU copies into it from a decoder's texture.</summary>
    Copy,

    /// <summary>The CPU writes into it from decoded planes.</summary>
    Upload,
}
