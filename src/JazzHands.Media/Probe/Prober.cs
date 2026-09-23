using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Media.Probe;

/// <summary>
/// Reads everything about a media file that the editor needs before decoding: streams, codecs,
/// colour signalling, HDR metadata, chapters, tags, and whether the frame timing is constant.
/// </summary>
/// <remarks>
/// In process through FFmpeg, never by shelling out to ffprobe. Probing is cheap enough to do on
/// import for every file, and the result is stored on the media item so it is done once.
/// </remarks>
public sealed unsafe class Prober
{
    /// <summary>How much of the file to scan when deciding whether frame timing is constant.</summary>
    public static readonly Flicks VariableFrameRateScanWindow = new(2 * Flicks.PerSecond);

    private readonly ILogger _log = Log.ForContext<Prober>();

    /// <summary>Probes a file. Throws <see cref="FfmpegException"/> when it cannot be opened.</summary>
    /// <param name="path">The media file.</param>
    /// <param name="detectFrameRateMode">
    /// Scan packet timestamps to tell constant from variable frame rate. Costs a couple of
    /// hundred packet reads; worth it on import, skippable when re-probing a known file.
    /// </param>
    public MediaProbe Probe(string path, bool detectFrameRateMode = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FfmpegLoader.Initialize();

        string fullPath = Path.GetFullPath(path);
        using var format = AvFormatContext.OpenInput(fullPath);
        AVFormatContext* context = format.Handle;

        var streams = new List<StreamInfo>((int)context->nb_streams);
        for (uint index = 0; index < context->nb_streams; index++)
        {
            streams.Add(ReadStream(context->streams[index], fullPath));
        }

        if (detectFrameRateMode)
        {
            DetectFrameRateModes(context, streams);
        }

        FillHdrFromBitstream(fullPath, streams);

        var probe = new MediaProbe(
            fullPath,
            Av.ReadString(context->iformat->name) ?? "unknown",
            Av.ReadString(context->iformat->long_name) ?? "unknown",
            FromContainerTime(context->duration),
            FromContainerTime(context->start_time),
            context->bit_rate,
            new FileInfo(fullPath).Length,
            streams,
            ReadChapters(context),
            Av.ReadDictionary(context->metadata));

        _log.Debug(
            "Probed {Path}: {Format}, {Duration}, {VideoStreams} video, {AudioStreams} audio, {SubtitleStreams} subtitle",
            fullPath,
            probe.FormatName,
            Timecode.FormatClock(probe.Duration),
            probe.VideoStreams.Count(),
            probe.AudioStreams.Count(),
            probe.SubtitleStreams.Count());

        return probe;
    }

    private static StreamInfo ReadStream(AVStream* stream, string path)
    {
        AVCodecParameters* parameters = stream->codecpar;
        var timeBase = new Rational(stream->time_base.num, stream->time_base.den);
        IReadOnlyDictionary<string, string> tags = Av.ReadDictionary(stream->metadata);

        AVCodec* codec = ffmpeg.avcodec_find_decoder(parameters->codec_id);
        string codecName = ffmpeg.avcodec_get_name(parameters->codec_id) ?? "unknown";
        string codecLongName = codec is not null
            ? Av.ReadString(codec->long_name) ?? codecName
            : codecName;

        string? profile = parameters->profile == ffmpeg.AV_PROFILE_UNKNOWN
            ? null
            : ffmpeg.avcodec_profile_name(parameters->codec_id, parameters->profile);

        StreamKind kind = parameters->codec_type switch
        {
            AVMediaType.AVMEDIA_TYPE_VIDEO => StreamKind.Video,
            AVMediaType.AVMEDIA_TYPE_AUDIO => StreamKind.Audio,
            AVMediaType.AVMEDIA_TYPE_SUBTITLE => StreamKind.Subtitle,
            AVMediaType.AVMEDIA_TYPE_ATTACHMENT => StreamKind.Attachment,
            AVMediaType.AVMEDIA_TYPE_DATA => StreamKind.Data,
            _ => StreamKind.Unknown,
        };

        return new StreamInfo(
            stream->index,
            kind,
            codecName,
            codecLongName,
            profile,
            FromStreamTime(stream->duration, stream->time_base),
            FromStreamTime(stream->start_time, stream->time_base),
            timeBase,
            parameters->bit_rate,
            (stream->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) != 0,
            tags.GetValueOrDefault("language"),
            tags.GetValueOrDefault("title") ?? tags.GetValueOrDefault("handler_name"),
            tags,
            kind == StreamKind.Video ? ReadVideo(stream, parameters, path) : null,
            kind == StreamKind.Audio ? ReadAudio(parameters) : null);
    }

    private static VideoStreamInfo ReadVideo(AVStream* stream, AVCodecParameters* parameters, string path)
    {
        var pixelFormat = (AVPixelFormat)parameters->format;
        AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(pixelFormat);

        int bitDepth = descriptor is not null ? descriptor->comp[0].depth : 8;
        bool hasAlpha = descriptor is not null &&
            (descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_ALPHA) != 0;

        AVRational guessed = ffmpeg.av_guess_frame_rate(null, stream, null);
        Rational frameRate = guessed.den > 0
            ? new Rational(guessed.num, guessed.den)
            : new Rational(stream->r_frame_rate.num, Math.Max(1, stream->r_frame_rate.den));
        Rational averageFrameRate = stream->avg_frame_rate.den > 0
            ? new Rational(stream->avg_frame_rate.num, stream->avg_frame_rate.den)
            : frameRate;

        Rational sampleAspect = parameters->sample_aspect_ratio.num > 0 && parameters->sample_aspect_ratio.den > 0
            ? new Rational(parameters->sample_aspect_ratio.num, parameters->sample_aspect_ratio.den)
            : Rational.One;

        var color = new ColorInfo(
            DescribeOrDefault(ffmpeg.av_color_primaries_name(parameters->color_primaries), "bt709"),
            DescribeOrDefault(ffmpeg.av_color_transfer_name(parameters->color_trc), "bt709"),
            DescribeOrDefault(ffmpeg.av_color_space_name(parameters->color_space), "bt709"),
            parameters->color_range == AVColorRange.AVCOL_RANGE_JPEG,
            DescribeOrDefault(ffmpeg.av_chroma_location_name(parameters->chroma_location), "left"));

        bool interlaced = parameters->field_order is
            AVFieldOrder.AV_FIELD_TT or
            AVFieldOrder.AV_FIELD_BB or
            AVFieldOrder.AV_FIELD_TB or
            AVFieldOrder.AV_FIELD_BT;

        return new VideoStreamInfo(
            parameters->width,
            parameters->height,
            ffmpeg.av_get_pix_fmt_name(pixelFormat) ?? "unknown",
            bitDepth,
            frameRate,
            averageFrameRate,
            sampleAspect,
            stream->nb_frames,
            interlaced,
            FrameRateMode.Unknown,
            color,
            ReadHdr(stream),
            hasAlpha);
    }

    /// <summary>
    /// Finds HDR mastering metadata that lives in the bitstream rather than the container.
    /// </summary>
    /// <remarks>
    /// x265 writes mastering display and content light level as SEI, and so do most real HDR
    /// captures, so a probe that only reads container boxes finds nothing and the compositor tone
    /// maps against guesses. Decoding a single frame settles it. Only files that already say they
    /// are HDR pay the cost, and only when the container was silent.
    /// </remarks>
    private void FillHdrFromBitstream(string path, List<StreamInfo> streams)
    {
        for (int i = 0; i < streams.Count; i++)
        {
            StreamInfo info = streams[i];
            if (info.Video is not { Hdr: null, Color.IsHdr: true })
            {
                continue;
            }

            try
            {
                HdrMetadata? metadata = ReadHdrByDecodingOneFrame(path, info.Index);
                if (metadata is not null)
                {
                    streams[i] = info with { Video = info.Video with { Hdr = metadata } };
                    _log.Debug(
                        "Read HDR mastering metadata from the bitstream of stream {Index} in {Path}",
                        info.Index,
                        path);
                }
            }
            catch (FfmpegException ex)
            {
                // A probe that cannot decode is still a useful probe; the clip will simply tone
                // map against the transfer function alone.
                _log.Information(
                    "Could not read HDR metadata from the bitstream of {Path}: {Reason}",
                    path,
                    ex.Message);
            }
        }
    }

    private static HdrMetadata? ReadHdrByDecodingOneFrame(string path, int streamIndex)
    {
        using var demuxer = new Decode.Demuxer(path);
        using var decoder = new Decode.VideoDecoder(demuxer, streamIndex, hardware: null, poolDepth: 1);
        using Decode.VideoFrame? frame = decoder.ReadFrame();

        return frame is null ? null : ReadHdrFromFrame(frame.Handle);
    }

    private static HdrMetadata? ReadHdrFromFrame(AVFrame* frame)
    {
        double minLuminance = 0;
        double maxLuminance = 0;
        int maxCll = 0;
        int maxFall = 0;
        bool found = false;

        AVFrameSideData* mastering = ffmpeg.av_frame_get_side_data(
            frame,
            AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
        if (mastering is not null)
        {
            var metadata = (AVMasteringDisplayMetadata*)mastering->data;
            if (metadata->has_luminance != 0)
            {
                minLuminance = ToDouble(metadata->min_luminance);
                maxLuminance = ToDouble(metadata->max_luminance);
                found = true;
            }
        }

        AVFrameSideData* light = ffmpeg.av_frame_get_side_data(
            frame,
            AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
        if (light is not null)
        {
            var content = (AVContentLightMetadata*)light->data;
            maxCll = (int)content->MaxCLL;
            maxFall = (int)content->MaxFALL;
            found = true;
        }

        return found ? new HdrMetadata(minLuminance, maxLuminance, maxCll, maxFall) : null;
    }

    private static HdrMetadata? ReadHdr(AVStream* stream)
    {
        double minLuminance = 0;
        double maxLuminance = 0;
        int maxCll = 0;
        int maxFall = 0;
        bool found = false;

        for (int i = 0; i < stream->codecpar->nb_coded_side_data; i++)
        {
            AVPacketSideData* side = &stream->codecpar->coded_side_data[i];
            switch (side->type)
            {
                case AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA:
                {
                    AVMasteringDisplayMetadata* metadata = (AVMasteringDisplayMetadata*)side->data;
                    if (metadata->has_luminance != 0)
                    {
                        minLuminance = ToDouble(metadata->min_luminance);
                        maxLuminance = ToDouble(metadata->max_luminance);
                        found = true;
                    }

                    break;
                }

                case AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL:
                {
                    AVContentLightMetadata* light = (AVContentLightMetadata*)side->data;
                    maxCll = (int)light->MaxCLL;
                    maxFall = (int)light->MaxFALL;
                    found = true;
                    break;
                }

                default:
                    break;
            }
        }

        return found ? new HdrMetadata(minLuminance, maxLuminance, maxCll, maxFall) : null;
    }

    private static AudioStreamInfo ReadAudio(AVCodecParameters* parameters)
    {
        const int layoutBufferSize = 128;
        byte* layoutBuffer = stackalloc byte[layoutBufferSize];
        AVChannelLayout layout = parameters->ch_layout;
        string layoutName = ffmpeg.av_channel_layout_describe(&layout, layoutBuffer, layoutBufferSize) >= 0
            ? Av.ReadString(layoutBuffer) ?? "unknown"
            : "unknown";

        var sampleFormat = (AVSampleFormat)parameters->format;

        return new AudioStreamInfo(
            parameters->sample_rate,
            parameters->ch_layout.nb_channels,
            layoutName,
            ffmpeg.av_get_sample_fmt_name(sampleFormat) ?? "unknown",
            parameters->bits_per_raw_sample);
    }

    private static IReadOnlyList<ChapterInfo> ReadChapters(AVFormatContext* context)
    {
        if (context->nb_chapters == 0)
        {
            return [];
        }

        var chapters = new List<ChapterInfo>((int)context->nb_chapters);
        for (uint i = 0; i < context->nb_chapters; i++)
        {
            AVChapter* chapter = context->chapters[i];
            IReadOnlyDictionary<string, string> tags = Av.ReadDictionary(chapter->metadata);
            chapters.Add(new ChapterInfo(
                chapter->id,
                FromStreamTime(chapter->start, chapter->time_base),
                FromStreamTime(chapter->end, chapter->time_base),
                tags.GetValueOrDefault("title")));
        }

        return chapters;
    }

    /// <summary>
    /// Reads packet timestamps for the first couple of seconds of each video stream and decides
    /// whether the timing is constant. Phone footage and screen capture are routinely variable,
    /// and the whole timeline assumes a fixed frame grid, so this decides whether import must
    /// conform the file.
    /// </summary>
    private void DetectFrameRateModes(AVFormatContext* context, List<StreamInfo> streams)
    {
        int[] videoIndexes = [.. streams.Where(s => s.Kind == StreamKind.Video).Select(s => s.Index)];
        if (videoIndexes.Length == 0)
        {
            return;
        }

        // Collect presentation timestamps rather than deltas: packets arrive in decode order, so
        // with B-frames consecutive pts values go backwards and forwards. Sorting first is the
        // difference between measuring frame timing and measuring the GOP structure.
        var timestamps = new Dictionary<int, List<long>>();
        foreach (int index in videoIndexes)
        {
            timestamps[index] = new List<long>(256);
        }
        using var packet = new AvPacket();
        int packetsRead = 0;

        while (packetsRead < 2000)
        {
            int result = ffmpeg.av_read_frame(context, packet.Handle);
            if (result < 0)
            {
                break;
            }

            packetsRead++;
            int index = packet.Handle->stream_index;
            long pts = packet.Handle->pts;
            try
            {
                if (!timestamps.TryGetValue(index, out List<long>? stream) || pts == ffmpeg.AV_NOPTS_VALUE)
                {
                    continue;
                }

                stream.Add(pts);

                AVRational timeBase = context->streams[index]->time_base;
                if (stream.Count > 8 && pts * timeBase.num / (double)timeBase.den > 2.0)
                {
                    break;
                }
            }
            finally
            {
                packet.Unref();
            }
        }

        // Rewind: the caller may go on to decode, and a probe should not consume the stream.
        ffmpeg.av_seek_frame(context, -1, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);

        for (int i = 0; i < streams.Count; i++)
        {
            StreamInfo info = streams[i];
            if (info.Video is null || !timestamps.TryGetValue(info.Index, out List<long>? measured))
            {
                continue;
            }

            FrameRateMode mode = ClassifyTimestamps(measured, ReorderDepth(context, info.Index));
            streams[i] = info with { Video = info.Video with { FrameRateMode = mode } };

            if (mode == FrameRateMode.Variable)
            {
                _log.Information(
                    "Stream {Index} has variable frame timing and will need conforming on import",
                    info.Index);
            }
        }
    }

    /// <summary>How many frames a stream may hold back before presenting one.</summary>
    private static int ReorderDepth(AVFormatContext* context, int streamIndex)
    {
        for (uint index = 0; index < context->nb_streams; index++)
        {
            AVStream* stream = context->streams[index];
            if (stream->index == streamIndex)
            {
                return Math.Max(0, stream->codecpar->video_delay);
            }
        }

        return 0;
    }

    /// <summary>
    /// Constant when every packet interval matches the median within one percent. Encoders round
    /// timestamps, so exact equality is too strict; a genuinely variable file is off by far more.
    /// </summary>
    /// <remarks>
    /// The scan stops after a couple of seconds, part way through a group of pictures, and
    /// packets arrive in decode order. With B-frames that means the last few presentation
    /// timestamps collected have holes in them: the frames that fill the holes are in packets
    /// that would have been read next. Sorted, a hole is indistinguishable from a doubled
    /// interval, so every file with a reorder delay looked variable. The tail is trimmed by the
    /// reorder depth, which bounds how far ahead a collected timestamp can be of a complete one.
    /// </remarks>
    private static FrameRateMode ClassifyTimestamps(List<long> timestamps, int reorderDepth)
    {
        if (timestamps.Count < 8)
        {
            return FrameRateMode.Unknown;
        }

        long[] ordered = [.. timestamps];
        Array.Sort(ordered);

        int complete = ordered.Length - reorderDepth;
        if (complete < 8)
        {
            return FrameRateMode.Unknown;
        }

        ordered = ordered[..complete];

        var deltas = new List<long>(ordered.Length);
        for (int i = 1; i < ordered.Length; i++)
        {
            long delta = ordered[i] - ordered[i - 1];
            if (delta > 0)
            {
                deltas.Add(delta);
            }
        }

        if (deltas.Count < 8)
        {
            return FrameRateMode.Unknown;
        }

        long[] sorted = [.. deltas];
        Array.Sort(sorted);
        long median = sorted[sorted.Length / 2];
        if (median <= 0)
        {
            return FrameRateMode.Unknown;
        }

        double tolerance = Math.Max(1.0, median * 0.01);
        foreach (long delta in deltas)
        {
            if (Math.Abs(delta - median) > tolerance)
            {
                return FrameRateMode.Variable;
            }
        }

        return FrameRateMode.Constant;
    }

    private static string DescribeOrDefault(string? value, string fallback)
    {
        return string.IsNullOrEmpty(value) || value.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? fallback
            : value;
    }

    private static Flicks FromContainerTime(long value) => value == ffmpeg.AV_NOPTS_VALUE
        ? Flicks.Zero
        : Flicks.FromTimebase(value, 1, ffmpeg.AV_TIME_BASE);

    private static Flicks FromStreamTime(long value, AVRational timeBase) =>
        value == ffmpeg.AV_NOPTS_VALUE || timeBase.den <= 0
            ? Flicks.Zero
            : Flicks.FromTimebase(value, timeBase.num, timeBase.den);

    private static double ToDouble(AVRational value) => value.den == 0 ? 0 : (double)value.num / value.den;
}
