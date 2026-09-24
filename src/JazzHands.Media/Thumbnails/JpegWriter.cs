using FFmpeg.AutoGen;
using JazzHands.Media.Interop;

namespace JazzHands.Media.Thumbnails;

/// <summary>
/// Encodes small pictures as JPEG with FFmpeg's own encoder, one fixed size per writer.
/// </summary>
/// <remarks>
/// The engine is headless and cannot reach WPF's encoders, and a thumbnail kept as raw pixels is
/// twenty times the size of the same thumbnail as a JPEG: at 284 by 160, 180 KB against about 8.
/// FFmpeg is already loaded, and its encoder takes the planar 4:2:0 that swscale writes.
///
/// Thread affine, like every FFmpeg context.
/// </remarks>
internal sealed unsafe class JpegWriter : IDisposable
{
    /// <summary>JPEG quantiser scale: 2 is best, 31 worst. 4 is clean at thumbnail size.</summary>
    private const int Quantiser = 4;

    private readonly AvCodecContext _context;
    private readonly AvFrame _frame = new();
    private readonly AvPacket _packet = new();
    private bool _disposed;

    /// <summary>Opens an encoder for pictures of one size.</summary>
    public JpegWriter(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        FfmpegLoader.Initialize();

        Width = width;
        Height = height;

        AVCodec* codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MJPEG);
        if (codec is null)
        {
            throw new FfmpegException("This FFmpeg build has no JPEG encoder.");
        }

        _context = new AvCodecContext(codec);
        try
        {
            AVCodecContext* context = _context.Handle;
            context->width = width;
            context->height = height;

            // Full range 4:2:0 is what a JPEG is. FFmpeg 7 and later take it spelled as plain
            // YUV420P with the range set, and warn about the old YUVJ420P spelling.
            context->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            context->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            context->time_base = new AVRational { num = 1, den = 25 };
            context->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
            context->global_quality = ffmpeg.FF_QP2LAMBDA * Quantiser;
            context->thread_count = 1;

            Av.Check(ffmpeg.avcodec_open2(context, codec, null), "avcodec_open2", "mjpeg");

            AVFrame* frame = _frame.Handle;
            frame->width = width;
            frame->height = height;
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            frame->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            Av.Check(ffmpeg.av_frame_get_buffer(frame, 0), "av_frame_get_buffer");
        }
        catch
        {
            _context.Dispose();
            _frame.Dispose();
            _packet.Dispose();
            throw;
        }
    }

    /// <summary>The picture width.</summary>
    public int Width { get; }

    /// <summary>The picture height.</summary>
    public int Height { get; }

    /// <summary>The frame to draw into before <see cref="Encode"/>: full range planar 4:2:0.</summary>
    public AVFrame* Frame
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Av.Check(ffmpeg.av_frame_make_writable(_frame.Handle), "av_frame_make_writable");
            return _frame.Handle;
        }
    }

    /// <summary>Encodes what is in <see cref="Frame"/> and returns the file's bytes.</summary>
    public byte[] Encode()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        AVCodecContext* context = _context.Handle;
        Av.Check(ffmpeg.avcodec_send_frame(context, _frame.Handle), "avcodec_send_frame", "mjpeg");

        AVPacket* packet = _packet.Handle;
        Av.Check(ffmpeg.avcodec_receive_packet(context, packet), "avcodec_receive_packet", "mjpeg");

        try
        {
            return new ReadOnlySpan<byte>(packet->data, packet->size).ToArray();
        }
        finally
        {
            _packet.Unref();
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
        _packet.Dispose();
        _frame.Dispose();
        _context.Dispose();
    }
}
