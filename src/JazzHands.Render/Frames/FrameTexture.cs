using JazzHands.Core.Time;
using Vortice.Direct3D11;

namespace JazzHands.Render.Frames;

/// <summary>
/// One decoded frame, in textures this project owns.
/// </summary>
/// <remarks>
/// A decoder's own output cannot be held: a hardware frame is a slice of a texture array the
/// decoder allocated a fixed number of, so keeping eight of them stalls the decoder that produced
/// them. Anything that outlives the decode, which means everything in a cache, lives here instead.
///
/// One texture per plane rather than one array texture, because the two paths that fill them
/// disagree about arrays: a hardware copy comes from one slice of the decoder's array, and a
/// software upload writes a mapped resource. Separate textures also let the compositor bind a
/// plane without a view that selects a slice.
/// </remarks>
public sealed class FrameTexture : IDisposable
{
    private readonly ID3D11Texture2D[] _planes;
    private readonly FrameTexturePool? _pool;
    private bool _disposed;

    internal FrameTexture(ID3D11Texture2D[] planes, PixelLayout layout, int width, int height, FrameTexturePool? pool)
    {
        _planes = planes;
        _pool = pool;
        Layout = layout;
        Width = width;
        Height = height;
        Bytes = layout.BytesFor(width, height);
    }

    /// <summary>What the samples in these textures mean.</summary>
    public PixelLayout Layout { get; }

    /// <summary>The frame's width in pixels.</summary>
    public int Width { get; }

    /// <summary>The frame's height in pixels.</summary>
    public int Height { get; }

    /// <summary>Roughly what this frame costs in video memory, which is what a cache budgets by.</summary>
    public long Bytes { get; }

    /// <summary>Where the frame sits on the source timeline.</summary>
    public Flicks Pts { get; set; }

    /// <summary>How long it is shown.</summary>
    public Flicks Duration { get; set; }

    /// <summary>True while the textures are still valid.</summary>
    public bool IsValid => !_disposed;

    /// <summary>How many planes there are.</summary>
    public int PlaneCount => _planes.Length;

    /// <summary>One plane's texture, for binding or copying into.</summary>
    public ID3D11Texture2D Plane(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _planes.Length);

        return _planes[index];
    }

    /// <summary>Returns the textures to the pool they came from, or releases them.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_pool is not null)
        {
            _pool.Return(this, _planes);
            return;
        }

        foreach (ID3D11Texture2D plane in _planes)
        {
            plane.Dispose();
        }
    }

    /// <summary>The planes, for the pool that owns them.</summary>
    internal ID3D11Texture2D[] Planes => _planes;
}
