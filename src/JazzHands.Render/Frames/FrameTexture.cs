using JazzHands.Core.Time;
using Vortice.Direct3D;
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
/// A software upload is one texture per plane, because each plane is mapped and written on its
/// own. A hardware copy is one texture in the decoder's own format holding every plane, because
/// Direct3D 11 copies a video surface only into its own format (see
/// <see cref="PixelLayout.PackedFormat"/>). Either way, read a plane through
/// <see cref="CreateView"/>, which knows which it is.
/// </remarks>
public sealed class FrameTexture : IDisposable
{
    private readonly ID3D11Texture2D[] _textures;
    private readonly FrameTexturePool? _pool;
    private bool _disposed;

    internal FrameTexture(ID3D11Texture2D[] textures, PixelLayout layout, int width, int height, FrameTexturePool? pool)
    {
        _textures = textures;
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
    public int PlaneCount => Layout.PlaneCount;

    /// <summary>True when every plane lives in one texture in the layout's packed format.</summary>
    public bool IsPacked => _textures.Length == 1 && Layout.PlaneCount > 1;

    /// <summary>
    /// The texture holding one plane, for copying into. In a packed frame every plane is the same
    /// texture; bind a plane through <see cref="CreateView"/> rather than a default view of this.
    /// </summary>
    public ID3D11Texture2D Plane(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, PlaneCount);

        return _textures[Math.Min(index, _textures.Length - 1)];
    }

    /// <summary>A view that samples one plane in its plane format. The caller disposes it.</summary>
    public ID3D11ShaderResourceView CreateView(ID3D11Device device, int plane)
    {
        ArgumentNullException.ThrowIfNull(device);

        var description = new ShaderResourceViewDescription
        {
            Format = Layout.Planes[plane].Format,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
        };

        return device.CreateShaderResourceView(Plane(plane), description);
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
            _pool.Return(this, _textures);
            return;
        }

        foreach (ID3D11Texture2D texture in _textures)
        {
            texture.Dispose();
        }
    }
}
