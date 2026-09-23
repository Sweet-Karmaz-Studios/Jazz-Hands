using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Media.Encode;

/// <summary>What a video encoder is asked for. The encoder name decides how the rest is spelled.</summary>
/// <param name="Encoders">
/// FFmpeg encoder names to try in order, for example h264_nvenc then libx264. The first that opens
/// is used, and the fall back is logged and reported.
/// </param>
/// <param name="Width">Frame width.</param>
/// <param name="Height">Frame height.</param>
/// <param name="FrameRate">The constant output rate.</param>
/// <param name="Quality">
/// Constant quality: CRF for x264 and x265, CQ for NVENC. Lower is better; about 18 to 23 is
/// visually clean for delivery, 28 and up is a proof.
/// </param>
/// <param name="Bitrate">A target in bits per second instead of constant quality, or 0.</param>
/// <param name="Speed">Speed against quality: slow, medium, fast. Mapped to each encoder's own presets.</param>
/// <param name="GopLength">Frames between keyframes.</param>
/// <param name="BFrames">B-frames between references.</param>
/// <param name="Lossless">Encode without loss, ignoring quality and bitrate.</param>
public sealed record VideoEncoderSettings(
    IReadOnlyList<string> Encoders,
    int Width,
    int Height,
    Rational FrameRate,
    int Quality = 20,
    long Bitrate = 0,
    EncoderSpeed Speed = EncoderSpeed.Medium,
    int GopLength = 0,
    int BFrames = 2,
    bool Lossless = false);

/// <summary>Speed against quality, spelled the same for every encoder.</summary>
public enum EncoderSpeed
{
    /// <summary>For proofs: fast, larger, a little softer.</summary>
    Fast,

    /// <summary>The default balance.</summary>
    Medium,

    /// <summary>For delivery: slower, smaller at the same quality.</summary>
    Slow,
}

/// <summary>
/// One FFmpeg video encoder, fed CPU frames in NV12 or planar 4:2:0, writing packets to a muxer.
/// </summary>
/// <remarks>
/// Frames come from <see cref="CreateFrame"/> so they are the right size and format for this
/// encoder. A frame is reused after it has been sent: <see cref="EncoderFrame.MakeWritable"/>
/// copies it first if the encoder is still holding the buffer, which is FFmpeg's own rule for
/// reusing a frame and costs nothing when the encoder has already copied it, as NVENC and x264 do.
///
/// Every frame is tagged BT.709 limited range, which is what the compositor's output pass writes.
/// HDR export is Phase 22's.
///
/// Thread affine: open, encode and flush on one thread.
/// </remarks>
public sealed unsafe class VideoEncoder : IDisposable
{
    private readonly ILogger _log = Log.ForContext<VideoEncoder>();
    private readonly AvCodecContext _context;
    private readonly AvPacket _packet = new();
    private long _sent;

    private VideoEncoder(AvCodecContext context, string name, VideoEncoderSettings settings, AVPixelFormat format, IReadOnlyList<string> skipped)
    {
        _context = context;
        Name = name;
        Settings = settings;
        InputFormat = format;
        Skipped = skipped;
    }

    /// <summary>The encoder that opened, for example h264_nvenc.</summary>
    public string Name { get; }

    /// <summary>What was asked for.</summary>
    public VideoEncoderSettings Settings { get; }

    /// <summary>NV12 or YUV420P, whichever this encoder takes.</summary>
    public AVPixelFormat InputFormat { get; }

    /// <summary>True when frames want an interleaved chroma plane (NV12).</summary>
    public bool TakesNv12 => InputFormat == AVPixelFormat.AV_PIX_FMT_NV12;

    /// <summary>Encoders earlier in the chain that would not open, with why.</summary>
    public IReadOnlyList<string> Skipped { get; }

    /// <summary>The time base packets come out in: one frame.</summary>
    public Rational TimeBase => new(Settings.FrameRate.Den, Settings.FrameRate.Num);

    internal AVCodecContext* Handle => _context.Handle;

    /// <summary>Opens the first encoder in the chain that will open.</summary>
    /// <param name="settings">What to encode.</param>
    /// <param name="globalHeader">True when the container keeps parameter sets in its header, as MP4 does.</param>
    /// <exception cref="FfmpegException">When none of them opens.</exception>
    public static VideoEncoder Open(VideoEncoderSettings settings, bool globalHeader)
    {
        ArgumentNullException.ThrowIfNull(settings);
        FfmpegLoader.Initialize();

        var skipped = new List<string>();

        foreach (string name in settings.Encoders)
        {
            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(name);
            if (codec is null)
            {
                skipped.Add($"{name}: not in this FFmpeg build");
                continue;
            }

            AVPixelFormat format = name.StartsWith("libx265", StringComparison.Ordinal)
                ? AVPixelFormat.AV_PIX_FMT_YUV420P
                : AVPixelFormat.AV_PIX_FMT_NV12;

            var context = new AvCodecContext(codec);
            try
            {
                Configure(context.Handle, name, settings, format, globalHeader);

                AVDictionary* options = Options(name, settings);
                int result;
                try
                {
                    result = ffmpeg.avcodec_open2(context.Handle, codec, &options);
                }
                finally
                {
                    ffmpeg.av_dict_free(&options);
                }

                if (result < 0)
                {
                    skipped.Add($"{name}: {Av.DescribeError(result)}");
                    context.Dispose();
                    continue;
                }

                var encoder = new VideoEncoder(context, name, settings, format, skipped);
                if (skipped.Count > 0)
                {
                    encoder._log.Warning("Encoding with {Encoder}; skipped {Skipped}", name, skipped);
                }

                return encoder;
            }
            catch
            {
                context.Dispose();
                throw;
            }
        }

        throw new FfmpegException($"No video encoder would open: {string.Join("; ", skipped)}.");
    }

    /// <summary>A frame of the right size and format for this encoder, with its own buffers.</summary>
    public EncoderFrame CreateFrame() => new(Settings.Width, Settings.Height, InputFormat);

    /// <summary>Sends one frame at a frame index and writes whatever packets come out.</summary>
    public void Encode(EncoderFrame frame, long index, Muxer muxer, int stream)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(muxer);

        frame.Handle->pts = index;
        Av.Check(ffmpeg.avcodec_send_frame(_context.Handle, frame.Handle), "avcodec_send_frame", Name);
        _sent++;
        Drain(muxer, stream);
    }

    /// <summary>Drains the encoder at the end, writing the frames it was holding back.</summary>
    public void Flush(Muxer muxer, int stream)
    {
        ArgumentNullException.ThrowIfNull(muxer);

        int result = ffmpeg.avcodec_send_frame(_context.Handle, null);
        if (result != Av.EndOfFile)
        {
            Av.Check(result, "avcodec_send_frame", Name);
        }

        Drain(muxer, stream);
        _log.Debug("{Encoder} encoded {Frames} frames", Name, _sent);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _packet.Dispose();
        _context.Dispose();
    }

    private void Drain(Muxer muxer, int stream)
    {
        while (true)
        {
            int result = ffmpeg.avcodec_receive_packet(_context.Handle, _packet.Handle);
            if (result == Av.Again || result == Av.EndOfFile)
            {
                return;
            }

            Av.Check(result, "avcodec_receive_packet", Name);
            muxer.Write(_packet.Handle, stream, TimeBase);
        }
    }

    private static void Configure(AVCodecContext* context, string name, VideoEncoderSettings settings, AVPixelFormat format, bool globalHeader)
    {
        context->width = settings.Width;
        context->height = settings.Height;
        context->pix_fmt = format;
        context->time_base = new AVRational { num = (int)settings.FrameRate.Den, den = (int)settings.FrameRate.Num };
        context->framerate = new AVRational { num = (int)settings.FrameRate.Num, den = (int)settings.FrameRate.Den };
        context->sample_aspect_ratio = new AVRational { num = 1, den = 1 };
        context->gop_size = settings.GopLength > 0
            ? settings.GopLength
            : (int)Math.Max(1, Math.Round(settings.FrameRate.ToDouble() * 2));
        context->max_b_frames = Math.Max(0, settings.BFrames);

        context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        context->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        context->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        context->chroma_sample_location = AVChromaLocation.AVCHROMA_LOC_LEFT;

        if (settings.Bitrate > 0 && !settings.Lossless)
        {
            context->bit_rate = settings.Bitrate;
            context->rc_max_rate = settings.Bitrate * 3 / 2;
            context->rc_buffer_size = (int)Math.Min(int.MaxValue, settings.Bitrate * 2);
        }

        if (globalHeader)
        {
            context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        }

        // x264 and x265 pick their thread count; NVENC has none to pick.
        if (!name.Contains("nvenc", StringComparison.Ordinal))
        {
            context->thread_count = 0;
        }
    }

    private static void Set(AVDictionary** options, string key, string value) =>
        ffmpeg.av_dict_set(options, key, value, 0);

    private static AVDictionary* Options(string name, VideoEncoderSettings settings)
    {
        AVDictionary* options = null;


        string quality = settings.Quality.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (name.Contains("nvenc", StringComparison.Ordinal))
        {
            // The export-pipeline skill's settings: p5 with the HQ tune, adaptive quantization
            // both ways, a 32 frame lookahead and B-frames used as references where the codec
            // allows it.
            Set(&options, "preset", settings.Speed switch
            {
                EncoderSpeed.Fast => "p3",
                EncoderSpeed.Slow => "p6",
                _ => "p5",
            });

            if (settings.Lossless)
            {
                Set(&options, "tune", "lossless");
                return options;
            }

            Set(&options, "tune", "hq");
            Set(&options, "spatial-aq", "1");
            Set(&options, "temporal-aq", "1");
            Set(&options, "rc-lookahead", "32");
            Set(&options, "b_ref_mode", "middle");

            if (settings.Bitrate > 0)
            {
                Set(&options, "rc", "vbr");
                Set(&options, "multipass", "fullres");
            }
            else
            {
                Set(&options, "rc", "vbr");
                Set(&options, "cq", quality);
                Set(&options, "b", "0");
            }

            return options;
        }

        if (name.StartsWith("libx264", StringComparison.Ordinal))
        {
            Set(&options, "preset", settings.Speed switch
            {
                EncoderSpeed.Fast => "veryfast",
                EncoderSpeed.Slow => "slow",
                _ => "medium",
            });

            if (settings.Lossless)
            {
                Set(&options, "qp", "0");
            }
            else if (settings.Bitrate <= 0)
            {
                Set(&options, "crf", quality);
            }

            return options;
        }

        if (name.StartsWith("libx265", StringComparison.Ordinal))
        {
            Set(&options, "preset", settings.Speed switch
            {
                EncoderSpeed.Fast => "veryfast",
                EncoderSpeed.Slow => "slow",
                _ => "medium",
            });

            if (settings.Lossless)
            {
                Set(&options, "x265-params", "lossless=1:log-level=error");
            }
            else
            {
                if (settings.Bitrate <= 0)
                {
                    Set(&options, "crf", quality);
                }

                Set(&options, "x265-params", "log-level=error");
            }

            return options;
        }

        return options;
    }
}

/// <summary>A CPU frame for an encoder, reused across frames.</summary>
public sealed unsafe class EncoderFrame : IDisposable
{
    private readonly AvFrame _frame = new();

    internal EncoderFrame(int width, int height, AVPixelFormat format)
    {
        AVFrame* frame = _frame.Handle;
        frame->width = width;
        frame->height = height;
        frame->format = (int)format;
        frame->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        frame->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        frame->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        frame->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        frame->chroma_location = AVChromaLocation.AVCHROMA_LOC_LEFT;
        Av.Check(ffmpeg.av_frame_get_buffer(frame, 0), "av_frame_get_buffer");
        Width = width;
        Height = height;
        IsNv12 = format == AVPixelFormat.AV_PIX_FMT_NV12;
    }

    /// <summary>Frame width.</summary>
    public int Width { get; }

    /// <summary>Frame height.</summary>
    public int Height { get; }

    /// <summary>True for NV12 (luma, then interleaved chroma); false for three planes.</summary>
    public bool IsNv12 { get; }

    internal AVFrame* Handle => _frame.Handle;

    /// <summary>Makes sure the buffers are this frame's alone before they are written.</summary>
    public void MakeWritable() =>
        Av.Check(ffmpeg.av_frame_make_writable(_frame.Handle), "av_frame_make_writable");

    /// <summary>A plane's first byte.</summary>
    public IntPtr Plane(int plane) => (IntPtr)_frame.Handle->data[(uint)plane];

    /// <summary>A plane's row pitch in bytes.</summary>
    public int Stride(int plane) => _frame.Handle->linesize[(uint)plane];

    /// <inheritdoc />
    public void Dispose() => _frame.Dispose();
}
