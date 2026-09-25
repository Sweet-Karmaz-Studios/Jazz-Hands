using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Decode;

namespace JazzHands.Media.Analysis;

/// <summary>A picture's brightness, one byte a pixel, rows packed.</summary>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
/// <param name="Pixels">Width times height bytes, top row first.</param>
public sealed record LumaImage(int Width, int Height, byte[] Pixels)
{
    /// <summary>The brightness at a pixel, 0 to 255; the nearest edge pixel outside.</summary>
    public byte At(int x, int y) => Pixels[(Math.Clamp(y, 0, Height - 1) * Width) + Math.Clamp(x, 0, Width - 1)];
}

/// <summary>
/// Reads a video's frames as brightness on the CPU, for analysis that looks at pixels: point
/// tracking, finding what stays still.
/// </summary>
/// <remarks>
/// One of the named exceptions to frames staying on the GPU: analysis, not display. Decodes in
/// software through the seeker, so frames come in order cheaply and a jump back seeks, and turns
/// each into eight bit grey with swscale, which works out brightness the same way whatever the
/// frame's format.
/// </remarks>
public sealed class LumaReader : IDisposable
{
    private readonly Seeker _seeker;
    private PixelConverter? _converter;

    /// <summary>Opens a file's best video stream.</summary>
    public LumaReader(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _seeker = Seeker.Open(path);
    }

    /// <summary>The frame shown at a source time as brightness, or null past the end.</summary>
    public LumaImage? Read(Flicks sourceTime)
    {
        using VideoFrame? frame = _seeker.Seek(sourceTime);
        if (frame is null)
        {
            return null;
        }

        if (_converter is null || _converter.Width != frame.Width || _converter.Height != frame.Height || _converter.From != frame.PixelFormat)
        {
            _converter?.Dispose();
            _converter = new PixelConverter(frame.Width, frame.Height, frame.PixelFormat, AVPixelFormat.AV_PIX_FMT_GRAY8);
        }

        using VideoFrame grey = _converter.Convert(frame);
        byte[] pixels = new byte[grey.Width * grey.Height];
        ReadOnlySpan<byte> plane = grey.GetPlane(0);
        int stride = grey.GetStride(0);
        for (int row = 0; row < grey.Height; row++)
        {
            plane.Slice(row * stride, grey.Width).CopyTo(pixels.AsSpan(row * grey.Width));
        }

        return new LumaImage(grey.Width, grey.Height, pixels);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _converter?.Dispose();
        _seeker.Dispose();
    }
}
