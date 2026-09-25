using System.Globalization;
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
/// Constant quality: CRF for x264, x265, SVT-AV1 and VP9, CQ for NVENC. Lower is better; about 18
/// to 23 is visually clean for delivery in H.264, 28 and up is a proof.
/// </param>
/// <param name="Bitrate">A target in bits per second instead of constant quality, or 0.</param>
/// <param name="Speed">Speed against quality: slow, medium, fast. Mapped to each encoder's own presets.</param>
/// <param name="GopLength">Frames between keyframes; 1 for every frame a keyframe.</param>
/// <param name="BFrames">B-frames between references.</param>
/// <param name="Lossless">Encode without loss, ignoring quality and bitrate.</param>
/// <param name="PixelFormat">The FFmpeg pixel format to encode, or null for the encoder's usual one.</param>
/// <param name="Profile">The codec profile, spelled as the encoder spells it, or null.</param>
/// <param name="Level">The codec level, or null. For FFV1 it is the version.</param>
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
    bool Lossless = false,
    string? PixelFormat = null,
    string? Profile = null,
    string? Level = null);

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
/// One FFmpeg video encoder, fed CPU frames in NV12 or P010, writing packets to a muxer.
/// </summary>
/// <remarks>
/// <para>
/// Frames come from <see cref="CreateFrame"/> so they are the right size and format for this
/// encoder: NV12 when it encodes eight bits, P010 when it keeps more. That is what the compositor's
/// output pass writes, so the renderer copies rows and never converts. An encoder that wants
/// something else (4:2:2 for ProRes and DNxHR, planar 4:2:0 for x265 and SVT-AV1, RGB for PNG) is
/// handed a frame converted by swscale first, which for the same sampling is a deinterleave and for
/// RGB is the BT.709 limited range matrix undone into full range.
/// </para>
/// <para>
/// GIF is the one encoder with a pass of its own: a GIF has 256 colours, and the best 256 are only
/// known once every frame has been seen. Frames go through libavfilter's palettegen and paletteuse,
/// which hold them until the end, so a GIF's frames come out when the encoder is flushed. That
/// holds every frame in memory, which is fine for the few seconds a GIF is.
/// </para>
/// <para>
/// A frame is reused after it has been sent: <see cref="EncoderFrame.MakeWritable"/> copies it
/// first if the encoder is still holding the buffer, which is FFmpeg's own rule for reusing a
/// frame and costs nothing when the encoder has already copied it, as NVENC and x264 do.
/// </para>
/// <para>Thread affine: open, encode and flush on one thread.</para>
/// </remarks>
public sealed unsafe class VideoEncoder : IDisposable
{
    private readonly ILogger _log = Log.ForContext<VideoEncoder>();
    private readonly AvCodecContext _context;
    private readonly AvPacket _packet = new();
    private readonly SwsContext* _convert;
    private readonly AvFrame? _converted;
    private readonly PaletteGraph? _palette;
    private long _sent;

    private VideoEncoder(AvCodecContext context, string name, VideoEncoderSettings settings, AVPixelFormat input, AVPixelFormat encoded, IReadOnlyList<string> skipped)
    {
        _context = context;
        Name = name;
        Settings = settings;
        InputFormat = input;
        EncodedFormat = encoded;
        Skipped = skipped;

        if (encoded == AVPixelFormat.AV_PIX_FMT_PAL8)
        {
            _palette = new PaletteGraph(settings, input);
        }
        else if (encoded != input)
        {
            _convert = Av.CheckAlloc(
                ffmpeg.sws_getContext(settings.Width, settings.Height, input, settings.Width, settings.Height, encoded, (int)SwsFlags.SWS_BICUBIC, null, null, null),
                $"sws_getContext ({input} to {encoded})");

            // The frames are BT.709 limited range. swscale assumes BT.601 unless told, which
            // shifts every colour a little on the way to RGB; and RGB is full range.
            bool rgb = IsRgb(encoded);
            int_array4 bt709 = *(int_array4*)ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
            ffmpeg.sws_setColorspaceDetails(_convert, bt709, 0, bt709, rgb ? 1 : 0, 0, 1 << 16, 1 << 16);

            _converted = new AvFrame();
            AVFrame* frame = _converted.Handle;
            frame->width = settings.Width;
            frame->height = settings.Height;
            frame->format = (int)encoded;
            Tag(frame, encoded);
            Av.Check(ffmpeg.av_frame_get_buffer(frame, 0), "av_frame_get_buffer");
        }
    }

    /// <summary>The encoder that opened, for example h264_nvenc.</summary>
    public string Name { get; }

    /// <summary>What was asked for.</summary>
    public VideoEncoderSettings Settings { get; }

    /// <summary>What frames from <see cref="CreateFrame"/> are: NV12, or P010 for an encoder that keeps ten bits.</summary>
    public AVPixelFormat InputFormat { get; }

    /// <summary>What the encoder itself takes.</summary>
    public AVPixelFormat EncodedFormat { get; }

    /// <summary>True when frames want an interleaved eight bit chroma plane (NV12).</summary>
    public bool TakesNv12 => InputFormat == AVPixelFormat.AV_PIX_FMT_NV12;

    /// <summary>True when frames are P010: ten bits in the top of sixteen.</summary>
    public bool TakesP010 => InputFormat == AVPixelFormat.AV_PIX_FMT_P010LE;

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

            var context = new AvCodecContext(codec);
            try
            {
                AVPixelFormat encoded = ChooseFormat(context.Handle, codec, name, settings);
                AVPixelFormat input = Depth(encoded) > 8 ? AVPixelFormat.AV_PIX_FMT_P010LE : AVPixelFormat.AV_PIX_FMT_NV12;

                Configure(context.Handle, name, settings, encoded, globalHeader);

                AVDictionary* options = ToDictionary(EncoderOptions(name, settings));
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

                var encoder = new VideoEncoder(context, name, settings, input, encoded, skipped);
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

        AVFrame* sent = frame.Handle;
        sent->pts = index;

        if (_palette is not null)
        {
            _palette.Send(sent);
            _sent++;
            return;
        }

        if (_converted is not null)
        {
            AVFrame* converted = _converted.Handle;
            Av.Check(ffmpeg.av_frame_make_writable(converted), "av_frame_make_writable");
            Av.Check(
                ffmpeg.sws_scale(_convert, sent->data, sent->linesize, 0, Settings.Height, converted->data, converted->linesize),
                "sws_scale");
            converted->pts = index;
            sent = converted;
        }

        Av.Check(ffmpeg.avcodec_send_frame(_context.Handle, sent), "avcodec_send_frame", Name);
        _sent++;
        Drain(muxer, stream);
    }

    /// <summary>Drains the encoder at the end, writing the frames it was holding back.</summary>
    public void Flush(Muxer muxer, int stream)
    {
        ArgumentNullException.ThrowIfNull(muxer);

        if (_palette is not null)
        {
            // Every frame has been seen, so the palette can be made and the frames drawn with it.
            _palette.Finish();
            for (AVFrame* frame = _palette.Receive(); frame != null; frame = _palette.Receive())
            {
                Av.Check(ffmpeg.avcodec_send_frame(_context.Handle, frame), "avcodec_send_frame", Name);
                Drain(muxer, stream);
            }
        }

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
        _converted?.Dispose();
        _palette?.Dispose();

        if (_convert is not null)
        {
            ffmpeg.sws_freeContext(_convert);
        }
    }

    /// <summary>
    /// The encoder's private options for these settings, by FFmpeg option name.
    /// </summary>
    /// <remarks>
    /// Public so the ffmpeg.exe fallback passes exactly what the in-process encoder would have
    /// been given. If the two ever differed, bisecting an encoder problem with it would compare
    /// two different encodes.
    /// </remarks>
    public static IReadOnlyList<KeyValuePair<string, string>> EncoderOptions(string name, VideoEncoderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(settings);
        var options = new List<KeyValuePair<string, string>>();
        string quality = settings.Quality.ToString(CultureInfo.InvariantCulture);

        if (name.Contains("nvenc", StringComparison.Ordinal))
        {
            Nvenc(options, settings, quality);
        }
        else if (name.StartsWith("libx264", StringComparison.Ordinal))
        {
            Set(options, "preset", settings.Speed switch
            {
                EncoderSpeed.Fast => "veryfast",
                EncoderSpeed.Slow => "slow",
                _ => "medium",
            });

            if (settings.Lossless)
            {
                Set(options, "qp", "0");
            }
            else if (settings.Bitrate <= 0)
            {
                Set(options, "crf", quality);
            }
        }
        else if (name.StartsWith("libx265", StringComparison.Ordinal))
        {
            Set(options, "preset", settings.Speed switch
            {
                EncoderSpeed.Fast => "veryfast",
                EncoderSpeed.Slow => "slow",
                _ => "medium",
            });

            if (settings.Lossless)
            {
                Set(options, "x265-params", "lossless=1:log-level=error");
            }
            else
            {
                if (settings.Bitrate <= 0)
                {
                    Set(options, "crf", quality);
                }

                Set(options, "x265-params", "log-level=error");
            }
        }
        else if (name == "libsvtav1")
        {
            // SVT-AV1's presets run 0 (slowest) to 13; 8 is its own default and about real time
            // at 1080p on a desktop CPU.
            Set(options, "preset", settings.Speed switch
            {
                EncoderSpeed.Fast => "10",
                EncoderSpeed.Slow => "5",
                _ => "8",
            });

            if (settings.Bitrate <= 0)
            {
                Set(options, "crf", settings.Lossless ? "0" : quality);
            }

            Set(options, "svtav1-params", "enable-overlays=1:scd=1");
        }
        else if (name == "libvpx-vp9")
        {
            Set(options, "deadline", "good");
            Set(options, "cpu-used", settings.Speed switch
            {
                EncoderSpeed.Fast => "5",
                EncoderSpeed.Slow => "1",
                _ => "2",
            });
            Set(options, "row-mt", "1");

            if (settings.Lossless)
            {
                Set(options, "lossless", "1");
            }
            else if (settings.Bitrate <= 0)
            {
                // Constant quality in libvpx is a CRF with no bitrate at all.
                Set(options, "crf", quality);
                Set(options, "b", "0");
            }
        }
        else if (name == "dnxhd")
        {
            // DNxHR's lightest profile by default, which is what an editing proxy wants: intra
            // only, about 45 Mb/s at 1080p30, decoded on a CPU faster than anything long GOP.
            Set(options, "profile", settings.Profile ?? "dnxhr_lb");
        }
        else if (name == "prores_ks")
        {
            Set(options, "profile", settings.Profile ?? "3");

            // What Apple's own encoder writes, so Final Cut and QuickTime treat the file as theirs.
            Set(options, "vendor", "apl0");
        }
        else if (name == "ffv1")
        {
            // Version 3 with a checksum per slice, the archival setting: a damaged frame is found
            // and the rest decode.
            Set(options, "level", settings.Level ?? "3");
            Set(options, "slices", "16");
            Set(options, "slicecrc", "1");
            Set(options, "context", "1");
        }

        if (settings.Profile is { } profile && (name.Contains("nvenc", StringComparison.Ordinal) || name.StartsWith("libx26", StringComparison.Ordinal)))
        {
            Set(options, "profile", profile);
        }

        if (settings.Level is { } level && (name.Contains("nvenc", StringComparison.Ordinal) || name.StartsWith("libx264", StringComparison.Ordinal)))
        {
            Set(options, "level", level);
        }

        return options;
    }

    /// <summary>
    /// The pixel format an encoder is opened with: what was asked for if the encoder takes it, the
    /// renderer's own NV12 or P010 where that is the same picture, and otherwise whatever FFmpeg
    /// judges the nearest the encoder takes.
    /// </summary>
    private static AVPixelFormat ChooseFormat(AVCodecContext* context, AVCodec* codec, string name, VideoEncoderSettings settings)
    {
        AVPixelFormat wanted = settings.PixelFormat is { } requested
            ? ffmpeg.av_get_pix_fmt(requested)
            : name switch
            {
                "dnxhd" => AVPixelFormat.AV_PIX_FMT_YUV422P,
                "prores_ks" => AVPixelFormat.AV_PIX_FMT_YUV422P10LE,
                "png" => AVPixelFormat.AV_PIX_FMT_RGB24,
                "gif" => AVPixelFormat.AV_PIX_FMT_PAL8,
                _ => AVPixelFormat.AV_PIX_FMT_YUV420P,
            };

        if (wanted == AVPixelFormat.AV_PIX_FMT_NONE)
        {
            throw new FfmpegException($"'{settings.PixelFormat}' is not a pixel format FFmpeg knows.");
        }

        AVPixelFormat[] supported = Supported(context, codec);
        if (supported.Length == 0)
        {
            return wanted;
        }

        // The same picture in the layout the renderer already writes costs nothing to hand over.
        AVPixelFormat same = wanted switch
        {
            AVPixelFormat.AV_PIX_FMT_YUV420P => AVPixelFormat.AV_PIX_FMT_NV12,
            AVPixelFormat.AV_PIX_FMT_YUV420P10LE => AVPixelFormat.AV_PIX_FMT_P010LE,
            _ => wanted,
        };

        if (supported.Contains(same))
        {
            return same;
        }

        if (supported.Contains(wanted))
        {
            return wanted;
        }

        AVPixelFormat[] terminated = [.. supported, AVPixelFormat.AV_PIX_FMT_NONE];
        fixed (AVPixelFormat* list = terminated)
        {
            int loss;
            return ffmpeg.avcodec_find_best_pix_fmt_of_list(list, wanted, 0, &loss);
        }
    }

    private static AVPixelFormat[] Supported(AVCodecContext* context, AVCodec* codec)
    {
        void* configs = null;
        int count = 0;
        if (ffmpeg.avcodec_get_supported_config(context, codec, AVCodecConfig.AV_CODEC_CONFIG_PIX_FORMAT, 0, &configs, &count) < 0 || configs == null)
        {
            return [];
        }

        return [.. new ReadOnlySpan<AVPixelFormat>(configs, count)];
    }

    /// <summary>Bits a sample of a pixel format keeps.</summary>
    private static int Depth(AVPixelFormat format)
    {
        AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(format);
        return descriptor is null ? 8 : descriptor->comp[0].depth;
    }

    private static bool IsRgb(AVPixelFormat format)
    {
        AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(format);
        return descriptor is not null && (descriptor->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0;
    }

    /// <summary>Tags a frame with what the compositor writes: BT.709 limited range, or full range sRGB for RGB.</summary>
    internal static void Tag(AVFrame* frame, AVPixelFormat format)
    {
        frame->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        if (IsRgb(format) || format == AVPixelFormat.AV_PIX_FMT_PAL8)
        {
            frame->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1;
            frame->colorspace = AVColorSpace.AVCOL_SPC_RGB;
            frame->color_range = AVColorRange.AVCOL_RANGE_JPEG;
        }
        else
        {
            frame->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            frame->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            frame->color_range = AVColorRange.AVCOL_RANGE_MPEG;
            frame->chroma_location = AVChromaLocation.AVCHROMA_LOC_LEFT;
        }
    }

    private static void Nvenc(List<KeyValuePair<string, string>> options, VideoEncoderSettings settings, string quality)
    {
        // The export-pipeline skill's settings: p5 with the HQ tune, adaptive quantization both
        // ways, a 32 frame lookahead and B-frames used as references where the codec allows it.
        Set(options, "preset", settings.Speed switch
        {
            EncoderSpeed.Fast => "p3",
            EncoderSpeed.Slow => "p6",
            _ => "p5",
        });

        if (settings.Lossless)
        {
            Set(options, "tune", "lossless");
            return;
        }

        Set(options, "tune", "hq");
        Set(options, "spatial-aq", "1");
        Set(options, "temporal-aq", "1");
        Set(options, "rc-lookahead", "32");
        Set(options, "b_ref_mode", "middle");

        if (settings.Bitrate > 0)
        {
            // Two passes over each frame at full resolution: the first finds where the bits
            // should go, so a size target lands where it was aimed.
            Set(options, "rc", "vbr");
            Set(options, "multipass", "fullres");
        }
        else
        {
            Set(options, "rc", "vbr");
            Set(options, "cq", quality);
            Set(options, "b", "0");
        }
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
        context->max_b_frames = context->gop_size <= 1 ? 0 : Math.Max(0, settings.BFrames);

        bool rgb = IsRgb(format) || format == AVPixelFormat.AV_PIX_FMT_PAL8;
        context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        context->color_trc = rgb ? AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1 : AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        context->colorspace = rgb ? AVColorSpace.AVCOL_SPC_RGB : AVColorSpace.AVCOL_SPC_BT709;
        context->color_range = rgb ? AVColorRange.AVCOL_RANGE_JPEG : AVColorRange.AVCOL_RANGE_MPEG;
        if (!rgb)
        {
            context->chroma_sample_location = AVChromaLocation.AVCHROMA_LOC_LEFT;
        }

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

        // The software encoders pick their thread count; NVENC has none to pick.
        if (!name.Contains("nvenc", StringComparison.Ordinal))
        {
            context->thread_count = 0;
        }
    }

    private static void Set(List<KeyValuePair<string, string>> options, string key, string value) =>
        options.Add(new(key, value));

    private static AVDictionary* ToDictionary(IReadOnlyList<KeyValuePair<string, string>> options)
    {
        AVDictionary* dictionary = null;
        foreach ((string key, string value) in options)
        {
            ffmpeg.av_dict_set(&dictionary, key, value, 0);
        }

        return dictionary;
    }

    /// <summary>
    /// libavfilter's two GIF passes: palettegen reads every frame for the 256 colours that suit
    /// them best, then paletteuse draws each with those colours and error diffusion.
    /// </summary>
    private sealed class PaletteGraph : IDisposable
    {
        private AVFilterGraph* _graph;
        private readonly AVFilterContext* _source;
        private readonly AVFilterContext* _sink;
        private readonly AvFrame _out = new();

        public PaletteGraph(VideoEncoderSettings settings, AVPixelFormat input)
        {
            AVFilterGraph* graph = Av.CheckAlloc(ffmpeg.avfilter_graph_alloc(), "avfilter_graph_alloc");
            AVFilterInOut* inputs = null;
            AVFilterInOut* outputs = null;

            try
            {
                string arguments = string.Create(
                    CultureInfo.InvariantCulture,
                    $"video_size={settings.Width}x{settings.Height}:pix_fmt={(int)input}:time_base={settings.FrameRate.Den}/{settings.FrameRate.Num}:pixel_aspect=1/1:colorspace=bt709:range=tv");
                AVFilterContext* source = null;
                Av.Check(
                    ffmpeg.avfilter_graph_create_filter(&source, ffmpeg.avfilter_get_by_name("buffer"), "in", arguments, null, graph),
                    "avfilter_graph_create_filter (buffer)",
                    arguments);

                AVFilterContext* sink = null;
                Av.Check(
                    ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("buffersink"), "out", null, null, graph),
                    "avfilter_graph_create_filter (buffersink)");

                outputs = Av.CheckAlloc(ffmpeg.avfilter_inout_alloc(), "avfilter_inout_alloc");
                outputs->name = ffmpeg.av_strdup("in");
                outputs->filter_ctx = source;
                outputs->pad_idx = 0;
                outputs->next = null;

                inputs = Av.CheckAlloc(ffmpeg.avfilter_inout_alloc(), "avfilter_inout_alloc");
                inputs->name = ffmpeg.av_strdup("out");
                inputs->filter_ctx = sink;
                inputs->pad_idx = 0;
                inputs->next = null;

                // stats_mode=full weighs every frame alike, which suits the short loops GIFs are;
                // sierra2_4a is the dither that looks best without crawling from frame to frame.
                const string Chain = "format=rgb24,split[a][b];[a]palettegen=stats_mode=full[p];[b][p]paletteuse=dither=sierra2_4a";
                Av.Check(ffmpeg.avfilter_graph_parse_ptr(graph, Chain, &inputs, &outputs, null), "avfilter_graph_parse_ptr", Chain);
                Av.Check(ffmpeg.avfilter_graph_config(graph, null), "avfilter_graph_config", Chain);

                _graph = graph;
                _source = source;
                _sink = sink;
                graph = null;
            }
            finally
            {
                if (inputs is not null)
                {
                    ffmpeg.avfilter_inout_free(&inputs);
                }

                if (outputs is not null)
                {
                    ffmpeg.avfilter_inout_free(&outputs);
                }

                if (graph is not null)
                {
                    ffmpeg.avfilter_graph_free(&graph);
                }
            }
        }

        public void Send(AVFrame* frame) =>
            Av.Check(ffmpeg.av_buffersrc_add_frame_flags(_source, frame, Av.BufferSrcKeepRef), "av_buffersrc_add_frame_flags");

        public void Finish() =>
            Av.Check(ffmpeg.av_buffersrc_add_frame_flags(_source, null, 0), "av_buffersrc_add_frame_flags (end)");

        /// <summary>The next frame drawn with the palette, valid until the next call, or null when there are no more.</summary>
        public AVFrame* Receive()
        {
            ffmpeg.av_frame_unref(_out.Handle);
            int result = ffmpeg.av_buffersink_get_frame(_sink, _out.Handle);
            if (result == Av.Again || result == Av.EndOfFile)
            {
                return null;
            }

            Av.Check(result, "av_buffersink_get_frame");
            return _out.Handle;
        }

        public void Dispose()
        {
            _out.Dispose();
            if (_graph is not null)
            {
                AVFilterGraph* graph = _graph;
                _graph = null;
                ffmpeg.avfilter_graph_free(&graph);
            }
        }
    }
}

/// <summary>A CPU frame for an encoder, reused across frames: NV12, or P010 for ten bits.</summary>
public sealed unsafe class EncoderFrame : IDisposable
{
    private readonly AvFrame _frame = new();

    internal EncoderFrame(int width, int height, AVPixelFormat format)
    {
        AVFrame* frame = _frame.Handle;
        frame->width = width;
        frame->height = height;
        frame->format = (int)format;
        VideoEncoder.Tag(frame, format);
        Av.Check(ffmpeg.av_frame_get_buffer(frame, 0), "av_frame_get_buffer");
        Width = width;
        Height = height;
        IsP010 = format == AVPixelFormat.AV_PIX_FMT_P010LE;
    }

    /// <summary>Frame width.</summary>
    public int Width { get; }

    /// <summary>Frame height.</summary>
    public int Height { get; }

    /// <summary>True for P010: sixteen bit samples, luma then interleaved chroma. False for NV12, the same with bytes.</summary>
    public bool IsP010 { get; }

    /// <summary>Bytes a sample takes: one for NV12, two for P010.</summary>
    public int BytesPerSample => IsP010 ? 2 : 1;

    internal AVFrame* Handle => _frame.Handle;

    /// <summary>An NV12 frame with its own buffers, for writing somewhere other than an in-process encoder.</summary>
    public static EncoderFrame CreateNv12(int width, int height) => new(width, height, AVPixelFormat.AV_PIX_FMT_NV12);

    /// <summary>A P010 frame with its own buffers, for writing somewhere other than an in-process encoder.</summary>
    public static EncoderFrame CreateP010(int width, int height) => new(width, height, AVPixelFormat.AV_PIX_FMT_P010LE);

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
