using FFmpeg.AutoGen;
using JazzHands.Media.Interop;

namespace JazzHands.Media.Decode;

/// <summary>
/// Converts frames from one pixel format to another with swscale, on the CPU.
/// </summary>
/// <remarks>
/// This is a named exception to frames staying on the GPU, and a narrow one. Video decode never
/// comes through here: the compositor samples YUV planes and converts in a shader. It exists for
/// the formats no GPU can sample, which in practice means the planar and big endian layouts the
/// image codecs produce. See <see cref="ImageDecoder"/>.
/// </remarks>
public sealed unsafe class PixelConverter : IDisposable
{
    private readonly SwsContext* _context;
    private readonly FramePool _pool = new(4);
    private bool _disposed;

    /// <summary>Creates a converter for one fixed conversion.</summary>
    public PixelConverter(int width, int height, AVPixelFormat from, AVPixelFormat to)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        FfmpegLoader.Initialize();

        Width = width;
        Height = height;
        From = from;
        To = to;

        // Same size in and out, so no resampling happens and the filter choice does not matter;
        // point is the one that says so. Accurate rounding because the whole reason this class
        // exists is to not lose what a 16-bit or float image contains.
        const int flags = (int)(SwsFlags.SWS_POINT | SwsFlags.SWS_ACCURATE_RND);

        _context = Av.CheckAlloc(
            ffmpeg.sws_getContext(width, height, from, width, height, to, flags, null, null, null),
            $"sws_getContext ({from} to {to})");
    }

    /// <summary>The frame width this converter was built for.</summary>
    public int Width { get; }

    /// <summary>The frame height this converter was built for.</summary>
    public int Height { get; }

    /// <summary>The format frames come in as.</summary>
    public AVPixelFormat From { get; }

    /// <summary>The format frames go out as.</summary>
    public AVPixelFormat To { get; }

    /// <summary>
    /// Converts a frame, returning a new one the caller disposes. The source is untouched.
    /// </summary>
    public VideoFrame Convert(VideoFrame source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (source.Location != FrameLocation.Cpu)
        {
            throw new InvalidOperationException("swscale cannot read a Direct3D texture; download the frame first.");
        }

        if (source.Width != Width || source.Height != Height)
        {
            throw new ArgumentException(
                $"This converter is built for {Width}x{Height}, not {source.Width}x{source.Height}.",
                nameof(source));
        }

        AvFrame destination = _pool.Rent();
        try
        {
            AVFrame* output = destination.Handle;
            output->width = Width;
            output->height = Height;
            output->format = (int)To;
            Av.Check(ffmpeg.av_frame_get_buffer(output, 0), "av_frame_get_buffer");

            AVFrame* input = source.Handle;
            Av.Check(
                ffmpeg.sws_scale(_context, input->data, input->linesize, 0, Height, output->data, output->linesize),
                "sws_scale");

            Av.Check(ffmpeg.av_frame_copy_props(output, input), "av_frame_copy_props");
            output->format = (int)To;

            return new VideoFrame(destination, _pool, source.Pts, source.Duration, source.Color);
        }
        catch
        {
            _pool.Return(destination);
            throw;
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
        ffmpeg.sws_freeContext(_context);
        _pool.Dispose();
    }
}
