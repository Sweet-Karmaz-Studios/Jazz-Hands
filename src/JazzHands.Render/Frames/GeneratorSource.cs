using System.Numerics;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace JazzHands.Render.Frames;

/// <summary>What a generator draws.</summary>
public enum GeneratorKind
{
    /// <summary>One colour across the whole frame.</summary>
    Solid,

    /// <summary>A linear ramp between two colours.</summary>
    Gradient,
}

/// <summary>
/// A picture with no media behind it: a colour, or a ramp between two.
/// </summary>
/// <remarks>
/// Small on its own and useful out of proportion to its size. It gives the compositor something
/// to draw before the decode path is wired into it, it is what a colour matte and the background
/// of a title are made of, and it is a render target whose exact contents are known, which is
/// what a compositing test needs in order to assert anything at all.
///
/// Output is the working format the rest of the graph composites in: linear light, half float,
/// premultiplied alpha.
/// </remarks>
public sealed class GeneratorSource : IDisposable
{
    private readonly RenderDevice _device;
    private readonly Dictionary<Key, ID3D11Texture2D> _cache = [];
    private bool _disposed;

    /// <summary>Creates a generator over a device.</summary>
    /// <param name="device">The device textures are made on. Not owned.</param>
    public GeneratorSource(RenderDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    /// <summary>Textures generated and kept, since a solid colour does not change between frames.</summary>
    public int Cached => _cache.Count;

    /// <summary>A frame of one colour, in linear light.</summary>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    /// <param name="color">The colour, already linear. The caller converts if it has an sRGB value.</param>
    public ID3D11Texture2D Solid(int width, int height, Vector4 color) =>
        Get(new Key(GeneratorKind.Solid, width, height, color, color, 0));

    /// <summary>
    /// A frame ramping from one colour to another, in linear light.
    /// </summary>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    /// <param name="from">The colour at the start of the ramp.</param>
    /// <param name="to">The colour at the end.</param>
    /// <param name="angleDegrees">
    /// Which way the ramp runs. Zero is left to right, ninety is top to bottom.
    /// </param>
    public ID3D11Texture2D Gradient(int width, int height, Vector4 from, Vector4 to, float angleDegrees = 0) =>
        Get(new Key(GeneratorKind.Gradient, width, height, from, to, angleDegrees));

    /// <summary>Drops every generated texture.</summary>
    public void Clear()
    {
        foreach (ID3D11Texture2D texture in _cache.Values)
        {
            texture.Dispose();
        }

        _cache.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
    }

    private ID3D11Texture2D Get(Key key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(key.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(key.Height);

        if (_cache.TryGetValue(key, out ID3D11Texture2D? existing))
        {
            return existing;
        }

        ID3D11Texture2D made = Draw(key);
        _cache[key] = made;
        return made;
    }

    /// <summary>
    /// Fills the pixels on the CPU and uploads them once.
    /// </summary>
    /// <remarks>
    /// A shader would be the obvious way and the wrong one: a generator's contents do not change
    /// between frames, so the texture is made once and handed out after that. Doing it here keeps
    /// the render graph free of a pass that would run every frame to produce the same pixels.
    /// </remarks>
    private unsafe ID3D11Texture2D Draw(Key key)
    {
        int width = key.Width;
        int height = key.Height;

        // Half float RGBA, the working format the whole graph composites in.
        var pixels = new Half[width * height * 4];

        double radians = key.Angle * Math.PI / 180.0;
        double dirX = Math.Cos(radians);
        double dirY = Math.Sin(radians);

        // Normalise the ramp so it runs from 0 to 1 across the frame whichever way it points.
        double span = (Math.Abs(dirX) * (width - 1)) + (Math.Abs(dirY) * (height - 1));
        double originX = dirX < 0 ? width - 1 : 0;
        double originY = dirY < 0 ? height - 1 : 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float position = key.Kind == GeneratorKind.Solid || span <= 0
                    ? 0
                    : (float)Math.Clamp((((x - originX) * dirX) + ((y - originY) * dirY)) / span, 0, 1);

                Vector4 color = Vector4.Lerp(key.From, key.To, position);

                // Premultiplied, as everything downstream expects.
                int at = ((y * width) + x) * 4;
                pixels[at] = (Half)(color.X * color.W);
                pixels[at + 1] = (Half)(color.Y * color.W);
                pixels[at + 2] = (Half)(color.Z * color.W);
                pixels[at + 3] = (Half)color.W;
            }
        }

        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Immutable,
        };

        fixed (Half* data = pixels)
        {
            var initial = new SubresourceData((IntPtr)data, (uint)(width * 4 * sizeof(ushort)));
            return _device.Device.CreateTexture2D(description, [initial]);
        }
    }

    private readonly record struct Key(
        GeneratorKind Kind,
        int Width,
        int Height,
        Vector4 From,
        Vector4 To,
        float Angle);
}
