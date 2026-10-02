using Vortice.WIC;

namespace JazzHands.Render.Scene;

/// <summary>
/// PNG, JPEG and the other still formats Windows can read, decoded through WIC to eight bit RGBA
/// (Phase 48), for the textures inside a glTF model.
/// </summary>
public static class ImageDecoder
{
    private static readonly Lazy<IWICImagingFactory> Factory = new(() => new IWICImagingFactory());

    /// <summary>A picture's texels, or null when Windows cannot read it.</summary>
    /// <param name="key">What it is, for caches.</param>
    /// <param name="bytes">The file's bytes.</param>
    public static MeshTexture? Decode(string key, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        try
        {
            lock (Factory)
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using IWICStream wic = Factory.Value.CreateStream(stream);
                using IWICBitmapDecoder decoder = Factory.Value.CreateDecoderFromStream(wic, DecodeOptions.CacheOnDemand);
                using IWICBitmapFrameDecode frame = decoder.GetFrame(0);
                using IWICFormatConverter converter = Factory.Value.CreateFormatConverter();
                converter.Initialize(frame, PixelFormat.Format32bppRGBA);
                Vortice.Mathematics.SizeI size = converter.Size;
                int width = size.Width;
                int height = size.Height;
                if (width <= 0 || height <= 0 || (long)width * height > 64L * 1024 * 1024)
                {
                    return null;
                }

                byte[] rgba = new byte[width * height * 4];
                unsafe
                {
                    fixed (byte* texels = rgba)
                    {
                        converter.CopyPixels(new Vortice.Mathematics.RectI(0, 0, width, height), (uint)(width * 4), (uint)rgba.Length, (nint)texels);
                    }
                }

                return new MeshTexture(key, width, height, rgba);
            }
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return null;
        }
    }
}
