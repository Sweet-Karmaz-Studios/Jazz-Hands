using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Decode;

namespace JazzHands.Media.Analysis;

/// <summary>A picture as eight bit RGB, three bytes a pixel, rows packed.</summary>
/// <param name="Time">The source time it is shown from.</param>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
/// <param name="Pixels">Width times height times three bytes, top row first.</param>
public sealed record RgbImage(Flicks Time, int Width, int Height, byte[] Pixels);

/// <summary>
/// Reads a video's frames in order as RGB on the CPU at a size of its own, for networks that look
/// at whole pictures (background removal, Phase 43).
/// </summary>
/// <remarks>
/// One of the named exceptions to frames staying on the GPU: analysis, not display. Decodes in
/// software, in order, and scales with swscale's area averaging to the size asked for, converting
/// from whatever the frame's format is.
/// </remarks>
public sealed class RgbReader : IDisposable
{
    private readonly Seeker _seeker;
    private readonly int _width;
    private readonly int _height;
    private PixelConverter? _converter;

    /// <summary>Opens a file's best video stream, to read at a size.</summary>
    public RgbReader(string path, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _seeker = Seeker.Open(path);
        _width = width;
        _height = height;
    }

    /// <summary>The next frame, or null past the end.</summary>
    public RgbImage? ReadNext()
    {
        using VideoFrame? frame = _seeker.ReadNext();
        if (frame is null)
        {
            return null;
        }

        if (_converter is null || _converter.Width != frame.Width || _converter.Height != frame.Height || _converter.From != frame.PixelFormat)
        {
            _converter?.Dispose();
            _converter = new PixelConverter(frame.Width, frame.Height, frame.PixelFormat, AVPixelFormat.AV_PIX_FMT_RGB24, _width, _height);
        }

        using VideoFrame rgb = _converter.Convert(frame);
        byte[] pixels = new byte[_width * _height * 3];
        ReadOnlySpan<byte> plane = rgb.GetPlane(0);
        int stride = rgb.GetStride(0);
        for (int row = 0; row < _height; row++)
        {
            plane.Slice(row * stride, _width * 3).CopyTo(pixels.AsSpan(row * _width * 3));
        }

        return new RgbImage(frame.Pts, _width, _height, pixels);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _converter?.Dispose();
        _seeker.Dispose();
    }
}
