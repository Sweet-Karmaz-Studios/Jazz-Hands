using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Interop;
using JazzHands.Media.Probe;
using Serilog;

namespace JazzHands.Media.Decode;

/// <summary>Which decoder a <see cref="VideoDecoder"/> ended up using.</summary>
public enum DecodePath
{
    /// <summary>NVDEC through D3D11VA. Frames stay in GPU texture arrays.</summary>
    Hardware,

    /// <summary>libavcodec on the CPU. Frames come back as planes in system memory.</summary>
    Software,
}

/// <summary>What a decoder is for, which decides how it trades picture quality for latency.</summary>
public enum DecodeTuning
{
    /// <summary>Every frame at full quality, with frame threads across the machine in software.</summary>
    Playback,

    /// <summary>
    /// A picture a few hundred pixels wide, as soon as possible: one thread, so the first frame
    /// out does not wait for a pipeline of frame threads to fill, and no loop filter, whose
    /// smoothing nobody can see once the picture is scaled down twelve times.
    /// </summary>
    Thumbnail,
}

/// <summary>
/// Decodes one video stream, preferring D3D11VA so frames land in GPU memory the compositor can
/// sample without a copy, and falling back to software when the codec or profile is not supported.
/// </summary>
/// <remarks>
/// Thread-affine, like its <see cref="Demuxer"/>. Steady-state decoding allocates nothing on the
/// managed heap: frame shells come from a <see cref="FramePool"/> and the pixels never leave
/// unmanaged memory.
/// </remarks>
public sealed unsafe class VideoDecoder : IVideoSource
{
    private static readonly AVPixelFormat[] PreferredHardwareFormats = [AVPixelFormat.AV_PIX_FMT_D3D11];

    /// <summary>
    /// Held in a static field for the life of the process. FFmpeg keeps the function pointer, so
    /// a delegate that only lived on the stack would be collected and the decoder would call into
    /// freed memory the moment the stream format was understood.
    /// </summary>
    private static readonly AVCodecContext_get_format GetFormatDelegate = GetFormatCallback;

    private readonly ILogger _log = Log.ForContext<VideoDecoder>();
    private readonly Demuxer _demuxer;
    private readonly AvCodecContext _codec;
    private readonly FramePool _pool;
    private readonly int _streamIndex;
    private readonly Rational _timeBase;
    private readonly Rational _frameRate;
    private readonly ColorInfo _color;
    private bool _disposed;
    private bool _flushed;

    /// <summary>Opens a decoder for a stream of an already-open file.</summary>
    /// <param name="demuxer">The demuxer to pull packets from. Not owned.</param>
    /// <param name="streamIndex">The video stream to decode.</param>
    /// <param name="hardware">
    /// The shared Direct3D device context, or null to decode in software. Not owned.
    /// </param>
    /// <param name="poolDepth">
    /// Decoder surfaces to keep in flight. Small for a seek decoder, larger for the one following
    /// the playhead; see the hw-decode skill.
    /// </param>
    /// <param name="tuning">What the frames are for. Thumbnail decoding is software only.</param>
    public VideoDecoder(
        Demuxer demuxer,
        int streamIndex,
        HardwareDeviceContext? hardware,
        int poolDepth = 8,
        DecodeTuning tuning = DecodeTuning.Playback)
    {
        ArgumentNullException.ThrowIfNull(demuxer);

        _demuxer = demuxer;
        _streamIndex = streamIndex;
        _pool = new FramePool(poolDepth + 4);

        AVStream* stream = demuxer.GetStream(streamIndex);
        if (stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
        {
            throw new ArgumentException($"Stream {streamIndex} of '{demuxer.Path}' is not video.", nameof(streamIndex));
        }

        _timeBase = new Rational(stream->time_base.num, stream->time_base.den);
        _frameRate = demuxer.GetFrameRate(streamIndex);

        bool preferHardware = hardware is not null && !hardware.IsDisposed && tuning == DecodeTuning.Playback;
        AVCodec* codec = FindDecoder(stream->codecpar->codec_id, preferHardware);
        if (codec is null)
        {
            throw new FfmpegException(
                Av.DecoderNotFound,
                "avcodec_find_decoder",
                $"{ffmpeg.avcodec_get_name(stream->codecpar->codec_id)} in '{demuxer.Path}'");
        }

        CodecName = ffmpeg.avcodec_get_name(stream->codecpar->codec_id) ?? "unknown";
        _codec = new AvCodecContext(codec);
        AVCodecContext* context = _codec.Handle;

        Av.Check(
            ffmpeg.avcodec_parameters_to_context(context, stream->codecpar),
            "avcodec_parameters_to_context",
            demuxer.Path);

        context->pkt_timebase = stream->time_base;

        bool wantHardware = preferHardware && SupportsD3D11(codec);
        if (wantHardware)
        {
            context->hw_device_ctx = ffmpeg.av_buffer_ref(hardware!.Handle);
            context->get_format = GetFormatDelegate;

            // The decoder needs its own surfaces plus whatever the caller holds on to.
            context->extra_hw_frames = poolDepth;
            context->thread_count = 1;
        }
        else if (tuning == DecodeTuning.Thumbnail)
        {
            // Frame threads hold a frame back per thread, so a keyframe asked for on its own
            // comes out only after as many more have been decoded. Thumbnails run several
            // decoders side by side instead, one thread each.
            context->thread_count = 1;
            context->skip_loop_filter = AVDiscard.AVDISCARD_ALL;
            context->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
        }
        else
        {
            // Software: let libavcodec use frame and slice threads across the machine.
            context->thread_count = 0;
            context->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
        }

        Av.Check(ffmpeg.avcodec_open2(context, codec, null), "avcodec_open2", demuxer.Path);

        Path = wantHardware && context->hw_device_ctx is not null ? DecodePath.Hardware : DecodePath.Software;

        _color = ReadColor(stream->codecpar);
        Width = context->width;
        Height = context->height;

        _log.Debug(
            "Opened {Codec} decoder for stream {Stream} of {File} on the {Path} path",
            CodecName,
            streamIndex,
            demuxer.Path,
            Path);
    }

    /// <summary>Whether this decoder ended up on hardware or software.</summary>
    public DecodePath Path { get; private set; }

    /// <summary>The codec short name, for example hevc.</summary>
    public string CodecName { get; }

    /// <summary>Coded width.</summary>
    public int Width { get; }

    /// <summary>Coded height.</summary>
    public int Height { get; }

    /// <summary>The stream's frame rate, as an exact rational.</summary>
    public Rational FrameRate => _frameRate;

    /// <summary>Frames decoded since the decoder was opened.</summary>
    public long FramesDecoded { get; private set; }

    /// <summary>Frame shells the pool has ever allocated. Flat in steady state.</summary>
    public int PooledFrames => _pool.Allocated;

    /// <summary>
    /// Decodes the next frame, reading packets as needed.
    /// </summary>
    /// <returns>The frame, which the caller must dispose, or null at end of stream.</returns>
    public VideoFrame? ReadFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        AVCodecContext* context = _codec.Handle;

        while (true)
        {
            AvFrame frame = _pool.Rent();
            int receive = ffmpeg.avcodec_receive_frame(context, frame.Handle);
            if (receive == 0)
            {
                FramesDecoded++;
                return BuildFrame(frame);
            }

            _pool.Return(frame);

            if (receive == Av.EndOfFile || receive == ffmpeg.AVERROR_EOF)
            {
                return null;
            }

            if (receive != Av.Again && receive != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                Av.Check(receive, "avcodec_receive_frame", _demuxer.Path);
            }

            if (_flushed)
            {
                return null;
            }

            AVPacket* packet = _demuxer.ReadPacket(_streamIndex);
            if (packet is null)
            {
                // Null packet puts the decoder into drain mode so buffered frames come out.
                Av.Check(ffmpeg.avcodec_send_packet(context, null), "avcodec_send_packet (drain)", _demuxer.Path);
                _flushed = true;
                continue;
            }

            int send = ffmpeg.avcodec_send_packet(context, packet);
            if (send != Av.Again && send != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                Av.Check(send, "avcodec_send_packet", _demuxer.Path);
            }
        }
    }

    /// <summary>
    /// Drops everything buffered in the decoder. Call immediately after seeking, or the first
    /// frames out will be from before the seek.
    /// </summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ffmpeg.avcodec_flush_buffers(_codec.Handle);
        _flushed = false;
    }

    /// <inheritdoc />
    /// <remarks>The decoder keeps no output frame grid, so the resume position is not needed.</remarks>
    void IVideoSource.Flush(Flicks resumeAt) => Flush();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _codec.Dispose();
        _pool.Dispose();
    }

    /// <summary>
    /// Finds a decoder for a codec, preferring one that can decode on the GPU.
    /// </summary>
    /// <remarks>
    /// avcodec_find_decoder returns FFmpeg.s default, which for AV1 is libdav1d: excellent, and
    /// entirely on the CPU. Asking for hardware means looking through every decoder registered
    /// for the codec and taking the first that advertises a D3D11VA device configuration.
    /// </remarks>
    private static AVCodec* FindDecoder(AVCodecID codecId, bool preferHardware)
    {
        if (!preferHardware)
        {
            return ffmpeg.avcodec_find_decoder(codecId);
        }

        void* iterator = null;
        AVCodec* candidate;
        while ((candidate = ffmpeg.av_codec_iterate(&iterator)) is not null)
        {
            if (candidate->id != codecId || ffmpeg.av_codec_is_decoder(candidate) == 0)
            {
                continue;
            }

            if (SupportsD3D11(candidate))
            {
                return candidate;
            }
        }

        return ffmpeg.avcodec_find_decoder(codecId);
    }

    /// <summary>True when a decoder advertises D3D11VA through a hardware device context.</summary>
    private static bool SupportsD3D11(AVCodec* codec)
    {
        for (int index = 0; ; index++)
        {
            AVCodecHWConfig* config = ffmpeg.avcodec_get_hw_config(codec, index);
            if (config is null)
            {
                return false;
            }

            if (config->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA &&
                (config->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Picks the pixel format the decoder will produce. FFmpeg calls this once the stream is
    /// understood, offering a list; taking AV_PIX_FMT_D3D11 is what keeps frames on the GPU.
    /// </summary>
    private static AVPixelFormat GetFormatCallback(AVCodecContext* context, AVPixelFormat* formats)
    {
        for (AVPixelFormat* format = formats; *format != AVPixelFormat.AV_PIX_FMT_NONE; format++)
        {
            foreach (AVPixelFormat preferred in PreferredHardwareFormats)
            {
                if (*format == preferred)
                {
                    return preferred;
                }
            }
        }

        // No hardware format offered: this codec or profile is software only. Take the decoder's
        // first choice and let the caller notice the path changed.
        Log.ForContext<VideoDecoder>().Information(
            "The decoder did not offer a Direct3D 11 pixel format; falling back to software");

        return formats[0];
    }

    private VideoFrame BuildFrame(AvFrame frame)
    {
        AVFrame* raw = frame.Handle;

        if (raw->format != (int)AVPixelFormat.AV_PIX_FMT_D3D11 && Path == DecodePath.Hardware)
        {
            // get_format fell back after the decoder was opened.
            Path = DecodePath.Software;
            _log.Information("{Codec} decode fell back to software mid-stream", CodecName);
        }

        long timestamp = raw->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
            ? raw->best_effort_timestamp
            : raw->pts;

        Flicks pts = timestamp == ffmpeg.AV_NOPTS_VALUE
            ? Flicks.Zero
            : Flicks.FromTimebase(timestamp, _timeBase.Num, _timeBase.Den);

        Flicks duration = raw->duration > 0
            ? Flicks.FromTimebase(raw->duration, _timeBase.Num, _timeBase.Den)
            : Flicks.FromFrames(1, _frameRate);

        return new VideoFrame(frame, _pool, pts, duration, _color);
    }

    private static ColorInfo ReadColor(AVCodecParameters* parameters) => new(
        Describe(ffmpeg.av_color_primaries_name(parameters->color_primaries), "bt709"),
        Describe(ffmpeg.av_color_transfer_name(parameters->color_trc), "bt709"),
        Describe(ffmpeg.av_color_space_name(parameters->color_space), "bt709"),
        parameters->color_range == AVColorRange.AVCOL_RANGE_JPEG,
        Describe(ffmpeg.av_chroma_location_name(parameters->chroma_location), "left"));

    private static string Describe(string? value, string fallback) =>
        string.IsNullOrEmpty(value) || value.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? fallback
            : value;
}
