using System.Globalization;
using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Encode;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Media.SmartCut;

/// <summary>What a source video stream is, for an encoder to match.</summary>
/// <param name="Codec">h264, hevc or av1.</param>
/// <param name="Width">Picture width.</param>
/// <param name="Height">Picture height.</param>
/// <param name="PixelFormat">The decoded pixel format.</param>
/// <param name="FrameRate">The constant rate.</param>
/// <param name="Profile">FFmpeg's profile number, or unknown.</param>
/// <param name="Level">FFmpeg's level number (H.264: 42 for 4.2; HEVC: 30 times the level), or unknown.</param>
/// <param name="ReorderDepth">How many frames the stream reorders; the encoder may not reorder more.</param>
/// <param name="GopLength">Frames between the source's keyframes.</param>
/// <param name="Bitrate">The source's bitrate, for choosing a quality that keeps up with it, or 0.</param>
/// <param name="Color">The colour tags to write: primaries, transfer, matrix, range, chroma location.</param>
/// <param name="Signature">What its parameter sets describe.</param>
public sealed record MatchSource(
    string Codec,
    int Width,
    int Height,
    AVPixelFormat PixelFormat,
    Rational FrameRate,
    int Profile,
    int Level,
    int ReorderDepth,
    int GopLength,
    long Bitrate,
    (AVColorPrimaries Primaries, AVColorTransferCharacteristic Transfer, AVColorSpace Matrix, AVColorRange Range, AVChromaLocation Location) Color,
    StreamSignature Signature);

/// <summary>
/// An encoder made to match a source stream closely enough that its frames and the source's
/// packets play as one stream: the same codec, profile, chroma format, bit depth and size, a
/// level, the source's colour tags, and no reordering at all.
/// </summary>
/// <remarks>
/// <para>
/// Opened without a global header, so each piece it encodes starts with its own parameter sets in
/// band; the smart cutter puts the source's back before the next copied keyframe. Every piece is a
/// fresh encoder that starts with an IDR and is flushed at its end, so it is a whole group of
/// pictures on its own.
/// </para>
/// <para>
/// Quality: a few frames a cut are cheap, so they are encoded well above what the source spent
/// (x264 CRF 16, x265 CRF 18, NVENC CQ 18, SVT-AV1 CRF 20) rather than matched to its bitrate, so
/// the seam is never the visible part.
/// </para>
/// <para>Thread affine.</para>
/// </remarks>
public sealed unsafe class MatchedEncoder : IDisposable
{
    private readonly ILogger _log = Log.ForContext<MatchedEncoder>();
    private readonly AvCodecContext _context;
    private readonly AvPacket _packet = new();
    private readonly SwsContext* _convert;
    private readonly AvFrame? _converted;
    private readonly AVPixelFormat _input;

    private MatchedEncoder(AvCodecContext context, string name, AVPixelFormat input, AVPixelFormat encoded, MatchSource source)
    {
        _context = context;
        _input = input;
        Name = name;
        Source = source;

        if (encoded != input)
        {
            _convert = Av.CheckAlloc(
                ffmpeg.sws_getContext(source.Width, source.Height, input, source.Width, source.Height, encoded, (int)SwsFlags.SWS_POINT, null, null, null),
                $"sws_getContext ({input} to {encoded})");
            _converted = new AvFrame();
            AVFrame* frame = _converted.Handle;
            frame->width = source.Width;
            frame->height = source.Height;
            frame->format = (int)encoded;
            Av.Check(ffmpeg.av_frame_get_buffer(frame, 0), "av_frame_get_buffer");
        }
    }

    /// <summary>The encoder that opened.</summary>
    public string Name { get; }

    /// <summary>What it matches.</summary>
    public MatchSource Source { get; }

    /// <summary>How many frames it reorders by, as it said when it opened.</summary>
    public int ReorderDepth => _context.Handle->has_b_frames;

    /// <summary>With a global header, the parameter sets it would write, in Annex B (or OBUs for AV1).</summary>
    public ReadOnlySpan<byte> Extradata => _context.Handle->extradata is null
        ? []
        : new ReadOnlySpan<byte>(_context.Handle->extradata, _context.Handle->extradata_size);

    /// <summary>The encoders to try for a codec, the GPU first.</summary>
    public static IReadOnlyList<string> ChainFor(string codec) => codec switch
    {
        "h264" => ["h264_nvenc", "libx264"],
        "hevc" => ["hevc_nvenc", "libx265"],
        "av1" => ["av1_nvenc", "libsvtav1"],
        _ => [],
    };

    /// <summary>Opens the first encoder in a chain that takes the source, noting why others did not.</summary>
    /// <param name="source">What to match.</param>
    /// <param name="encoders">The encoders to try.</param>
    /// <param name="globalHeader">Put the parameter sets in <see cref="Extradata"/> instead of in band, to inspect them.</param>
    /// <param name="skipped">Filled with the ones that would not open, and why.</param>
    public static MatchedEncoder Open(MatchSource source, IReadOnlyList<string> encoders, bool globalHeader, List<string> skipped)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(encoders);
        ArgumentNullException.ThrowIfNull(skipped);
        FfmpegLoader.Initialize();

        foreach (string name in encoders)
        {
            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(name);
            if (codec is null)
            {
                skipped.Add($"{name}: not in this FFmpeg build");
                continue;
            }

            var context = new AvCodecContext(codec);
            AVPixelFormat encoded = Format(context.Handle, codec, source.PixelFormat);
            if (encoded == AVPixelFormat.AV_PIX_FMT_NONE)
            {
                skipped.Add($"{name}: does not take {ffmpeg.av_get_pix_fmt_name(source.PixelFormat)}");
                context.Dispose();
                continue;
            }

            Configure(context.Handle, name, source, encoded, globalHeader);
            AVDictionary* options = null;
            foreach ((string key, string value) in Options(name, source))
            {
                ffmpeg.av_dict_set(&options, key, value, 0);
            }

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

            if (context.Handle->has_b_frames > source.ReorderDepth)
            {
                skipped.Add($"{name}: reorders {context.Handle->has_b_frames} frames where the source reorders {source.ReorderDepth}");
                context.Dispose();
                continue;
            }

            return new MatchedEncoder(context, name, source.PixelFormat, encoded, source);
        }

        throw new FfmpegException($"No encoder matches the source: {string.Join("; ", skipped)}.");
    }

    /// <summary>
    /// Whether a chain can match a source: opens it with a global header, reads the parameter sets
    /// it would write, and compares what they describe with the source's.
    /// </summary>
    /// <returns>The encoder that matched and null, or null and why none did.</returns>
    public static (string? Encoder, string? Reason) Check(MatchSource source, IReadOnlyList<string> encoders)
    {
        ArgumentNullException.ThrowIfNull(source);
        var skipped = new List<string>();
        var remaining = new List<string>(encoders);
        while (remaining.Count > 0)
        {
            MatchedEncoder encoder;
            try
            {
                encoder = Open(source, remaining, globalHeader: true, skipped);
            }
            catch (FfmpegException error)
            {
                return (null, error.Message);
            }

            using (encoder)
            {
                StreamSignature? written = ParameterSets.FromAnnexB(source.Codec, encoder.Extradata);
                string? differences = written is null ? "its parameter sets could not be read" : source.Signature.Differences(written);
                if (differences is null)
                {
                    return (encoder.Name, null);
                }

                skipped.Add($"{encoder.Name}: writes {differences}");
                remaining.RemoveRange(0, remaining.IndexOf(encoder.Name) + 1);
            }
        }

        return (null, $"No encoder matches the source: {string.Join("; ", skipped)}.");
    }

    /// <summary>Sends a decoded frame, in the source's pixel format, at an output frame index; packets go to <paramref name="write"/>.</summary>
    public void Send(AVFrame* frame, long index, Action<nint> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        AVFrame* sent = frame;
        if (_converted is not null)
        {
            AVFrame* converted = _converted.Handle;
            Av.Check(ffmpeg.av_frame_make_writable(converted), "av_frame_make_writable");
            Av.Check(ffmpeg.sws_scale(_convert, frame->data, frame->linesize, 0, Source.Height, converted->data, converted->linesize), "sws_scale");
            sent = converted;
        }

        sent->pts = index;
        sent->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
        sent->color_primaries = Source.Color.Primaries;
        sent->color_trc = Source.Color.Transfer;
        sent->colorspace = Source.Color.Matrix;
        sent->color_range = Source.Color.Range;
        Av.Check(ffmpeg.avcodec_send_frame(_context.Handle, sent), "avcodec_send_frame", Name);
        Drain(write);
    }

    /// <summary>Drains the encoder, closing its group of pictures.</summary>
    public void Flush(Action<nint> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        int result = ffmpeg.avcodec_send_frame(_context.Handle, null);
        if (result != Av.EndOfFile)
        {
            Av.Check(result, "avcodec_send_frame", Name);
        }

        Drain(write);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _packet.Dispose();
        _converted?.Dispose();
        _context.Dispose();
        if (_convert is not null)
        {
            ffmpeg.sws_freeContext(_convert);
        }
    }

    private void Drain(Action<nint> write)
    {
        while (true)
        {
            int result = ffmpeg.avcodec_receive_packet(_context.Handle, _packet.Handle);
            if (result == Av.Again || result == Av.EndOfFile)
            {
                return;
            }

            Av.Check(result, "avcodec_receive_packet", Name);
            write((nint)_packet.Handle);
            ffmpeg.av_packet_unref(_packet.Handle);
        }
    }

    /// <summary>
    /// The pixel format to encode: the source's when the encoder takes it, NV12 or P010 for the same
    /// picture when it takes those instead (NVENC), and none when it takes neither.
    /// </summary>
    private static AVPixelFormat Format(AVCodecContext* context, AVCodec* codec, AVPixelFormat source)
    {
        void* configs = null;
        int count = 0;
        if (ffmpeg.avcodec_get_supported_config(context, codec, AVCodecConfig.AV_CODEC_CONFIG_PIX_FORMAT, 0, &configs, &count) < 0 || configs == null)
        {
            return source;
        }

        var supported = new ReadOnlySpan<AVPixelFormat>(configs, count);
        if (supported.Contains(source))
        {
            return source;
        }

        AVPixelFormat same = source switch
        {
            AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P => AVPixelFormat.AV_PIX_FMT_NV12,
            AVPixelFormat.AV_PIX_FMT_YUV420P10LE => AVPixelFormat.AV_PIX_FMT_P010LE,
            _ => AVPixelFormat.AV_PIX_FMT_NONE,
        };

        return same != AVPixelFormat.AV_PIX_FMT_NONE && supported.Contains(same) ? same : AVPixelFormat.AV_PIX_FMT_NONE;
    }

    private static void Configure(AVCodecContext* context, string name, MatchSource source, AVPixelFormat format, bool globalHeader)
    {
        context->width = source.Width;
        context->height = source.Height;
        context->pix_fmt = format;
        context->time_base = new AVRational { num = (int)source.FrameRate.Den, den = (int)source.FrameRate.Num };
        context->framerate = new AVRational { num = (int)source.FrameRate.Num, den = (int)source.FrameRate.Den };
        context->sample_aspect_ratio = new AVRational { num = 1, den = 1 };
        context->gop_size = Math.Max(1, source.GopLength);
        // No reordering in an encoded piece: every picture is shown as soon as it is decoded, so
        // none is still waiting when the piece ends. A CRA after an end of sequence throws away
        // whatever is waiting (HEVC infers no_output_of_prior_pics_flag for it), and B-frames here
        // would lose the last pictures of the piece. A few frames a cut do not miss them.
        context->max_b_frames = 0;
        context->color_primaries = source.Color.Primaries;
        context->color_trc = source.Color.Transfer;
        context->colorspace = source.Color.Matrix;
        context->color_range = source.Color.Range;
        context->chroma_sample_location = source.Color.Location;
        if (source.Profile >= 0)
        {
            context->profile = source.Profile;
        }

        // AV1's level is a sequence level index to FFmpeg and something else to SVT-AV1; the
        // encoder chooses its own, and the level is not part of what has to match.
        if (source.Level > 0 && source.Codec != "av1")
        {
            context->level = source.Level;
        }

        if (globalHeader)
        {
            context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        }

        context->flags |= unchecked((int)ffmpeg.AV_CODEC_FLAG_CLOSED_GOP);
        if (!name.Contains("nvenc", StringComparison.Ordinal))
        {
            context->thread_count = 0;
        }
    }

    private static List<KeyValuePair<string, string>> Options(string name, MatchSource source)
    {
        var options = new List<KeyValuePair<string, string>>();
        void Set(string key, string value) => options.Add(new(key, value));

        if (name.Contains("nvenc", StringComparison.Ordinal))
        {
            Set("preset", "p5");
            Set("tune", "hq");
            Set("rc", "vbr");
            Set("cq", "18");
            Set("b", "0");
            Set("bf", "0");
        }
        else if (name == "libx264")
        {
            Set("preset", "fast");
            Set("crf", "16");
        }
        else if (name == "libx265")
        {
            Set("preset", "fast");
            Set("crf", "18");
            Set("x265-params", string.Create(CultureInfo.InvariantCulture, $"log-level=error:open-gop=0:keyint={Math.Max(1, source.GopLength)}"));
        }
        else if (name == "libsvtav1")
        {
            Set("preset", "8");
            Set("crf", "20");
        }

        return options;
    }
}
