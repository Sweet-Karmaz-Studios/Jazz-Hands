using System.Diagnostics;
using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Decode;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Media.Encode;

/// <summary>One stretch of a source to copy: from a keyframe at <see cref="Start"/> up to <see cref="End"/>.</summary>
/// <param name="Path">The source file.</param>
/// <param name="Start">Where the stretch starts, in source time. Must be a keyframe of the video stream.</param>
/// <param name="End">Where it ends, exclusive: a keyframe, or the end of the file.</param>
public sealed record CopySegment(string Path, Flicks Start, Flicks End)
{
    /// <summary>How long the stretch lasts.</summary>
    public Flicks Duration => End - Start;
}

/// <summary>A stream copy: which streams to take and the stretches to join.</summary>
/// <param name="OutputPath">Where to write. The extension chooses the container unless <see cref="Container"/> says.</param>
/// <param name="Segments">The stretches, played one after another.</param>
/// <param name="VideoStream">The source video stream, or -1 for sound only.</param>
/// <param name="AudioStreams">The source audio streams, in the order they go into the output.</param>
/// <param name="FrameRate">
/// The video's constant frame rate, when it has one. Timestamps are then written as whole frames,
/// which a millisecond Matroska source cannot give by itself.
/// </param>
/// <param name="Container">The FFmpeg muxer name, or null to choose from the extension.</param>
/// <param name="FastStart">Put an MP4's index at the front.</param>
public sealed record StreamCopyJob(
    string OutputPath,
    IReadOnlyList<CopySegment> Segments,
    int VideoStream,
    IReadOnlyList<int> AudioStreams,
    Rational? FrameRate = null,
    string? Container = null,
    bool FastStart = true);

/// <summary>How far a copy has got.</summary>
/// <param name="Done">Source time copied so far.</param>
/// <param name="Total">Source time to copy.</param>
/// <param name="Bytes">Bytes written.</param>
public readonly record struct CopyProgress(Flicks Done, Flicks Total, long Bytes)
{
    /// <summary>Between zero and one.</summary>
    public double Fraction => Total.Value <= 0 ? 1.0 : Math.Clamp((double)Done.Value / Total.Value, 0.0, 1.0);
}

/// <summary>What a copy wrote.</summary>
/// <param name="Path">The output file.</param>
/// <param name="Bytes">Its size.</param>
/// <param name="Duration">How long it plays.</param>
/// <param name="VideoPackets">Video packets written.</param>
/// <param name="AudioPackets">Audio packets written, all streams together.</param>
/// <param name="Elapsed">How long it took.</param>
public sealed record StreamCopyResult(string Path, long Bytes, Flicks Duration, long VideoPackets, long AudioPackets, TimeSpan Elapsed);

/// <summary>
/// Joins stretches of source files into one output without decoding anything.
/// </summary>
/// <remarks>
/// Every stretch starts on a video keyframe, so the output decodes from its first packet and at
/// every join. The planner chooses the stretches; this copies them.
///
/// Timestamps are rebuilt rather than carried over, because the sources do not always have them:
/// Matroska stores presentation times in milliseconds and no decode times at all, and MP4 needs
/// both. Video presentation times are the source's moved to where the stretch sits in the output,
/// on the frame grid when the rate is constant. Decode times are derived from them: the k-th
/// packet in decode order decodes at the (k - d)-th smallest presentation time, where d is how
/// far the stream reorders. That is what the encoder did in the first place, it is monotonic, and
/// it never puts a decode after its presentation.
///
/// Audio is counted in samples. Each stretch's first packet is placed where its source time says,
/// relative to the stretch, and every packet after it follows on by its sample count, so a
/// millisecond source does not wobble by a sample at every packet. A packet that would overlap
/// the end of the previous stretch's sound starts where that ended instead: less than a packet of
/// drift, reset at every join.
///
/// Thread affine. Runs on the thread that calls <see cref="Copy"/>.
/// </remarks>
public static unsafe class StreamCopier
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(StreamCopier));

    /// <summary>Copies the stretches into one file.</summary>
    /// <param name="job">What to copy.</param>
    /// <param name="progress">Told about each packet batch; may be null.</param>
    /// <param name="cancellationToken">Stops the copy. The partial file is deleted.</param>
    public static StreamCopyResult Copy(
        StreamCopyJob job,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Segments.Count == 0)
        {
            throw new ArgumentException("There is nothing to copy.", nameof(job));
        }

        if (job.VideoStream < 0 && job.AudioStreams.Count == 0)
        {
            throw new ArgumentException("A copy needs at least one stream.", nameof(job));
        }

        var clock = Stopwatch.StartNew();
        Flicks total = Flicks.Zero;
        foreach (CopySegment segment in job.Segments)
        {
            total += segment.Duration;
        }

        var demuxers = new Dictionary<string, Demuxer>(StringComparer.OrdinalIgnoreCase);
        Muxer? muxer = null;
        bool complete = false;

        try
        {
            Demuxer first = Open(demuxers, job.Segments[0].Path);
            muxer = Muxer.Create(job.OutputPath, job.Container);
            Output output = AddStreams(muxer, first, job);
            muxer.WriteHeader(job.FastStart);

            Flicks offset = Flicks.Zero;
            using var packet = new AvPacket();

            foreach (CopySegment segment in job.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Demuxer demuxer = Open(demuxers, segment.Path);
                CopyOne(demuxer, segment, offset, output, muxer, packet, cancellationToken, progress, total);
                offset += segment.Duration;
            }

            muxer.Finish();
            complete = true;

            long bytes = new FileInfo(muxer.Path).Length;
            progress?.Report(new CopyProgress(total, total, bytes));

            Log.Information(
                "Copied {Segments} segments ({Duration}) into {Path}: {Bytes} bytes in {Elapsed} ms",
                job.Segments.Count,
                Timecode.FormatClock(total),
                muxer.Path,
                bytes,
                clock.ElapsedMilliseconds);

            return new StreamCopyResult(muxer.Path, bytes, total, output.VideoPackets, output.AudioPackets, clock.Elapsed);
        }
        finally
        {
            string? path = muxer?.Path;
            muxer?.Dispose();
            foreach (Demuxer demuxer in demuxers.Values)
            {
                demuxer.Dispose();
            }

            if (!complete && path is not null)
            {
                TryDelete(path);
            }
        }
    }

    private static Demuxer Open(Dictionary<string, Demuxer> demuxers, string path)
    {
        if (!demuxers.TryGetValue(path, out Demuxer? demuxer))
        {
            demuxer = new Demuxer(path);
            demuxers[path] = demuxer;
        }

        return demuxer;
    }

    private static Output AddStreams(Muxer muxer, Demuxer source, StreamCopyJob job)
    {
        var output = new Output();

        if (job.VideoStream >= 0)
        {
            AVStream* stream = source.GetStream(job.VideoStream);
            if (stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                throw new ArgumentException($"Stream {job.VideoStream} of '{source.Path}' is not video.", nameof(job));
            }

            Rational timeBase = job.FrameRate is { } rate
                ? new Rational(rate.Den, rate.Num)
                : new Rational(stream->time_base.num, stream->time_base.den);

            output.Video = new VideoLane(
                job.VideoStream,
                muxer.AddCopiedStream(stream->codecpar, timeBase, Av.ReadDictionary(stream->metadata)),
                timeBase,
                ReorderDepth(stream),
                job.FrameRate);
        }

        foreach (int index in job.AudioStreams)
        {
            AVStream* stream = source.GetStream(index);
            if (stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                throw new ArgumentException($"Stream {index} of '{source.Path}' is not audio.", nameof(job));
            }

            int rate = stream->codecpar->sample_rate;
            output.Audio.Add(new AudioLane(
                index,
                muxer.AddCopiedStream(stream->codecpar, new Rational(1, rate), Av.ReadDictionary(stream->metadata)),
                rate));
        }

        return output;
    }

    /// <summary>
    /// How many frames the stream reorders by. More than the truth is harmless, a decode time a
    /// little earlier than it needs to be; less is not, so the probe's figure has a floor of the
    /// B-frame depth H.264 and HEVC encoders use by default when the stream has B-frames at all.
    /// </summary>
    private static int ReorderDepth(AVStream* stream)
    {
        int delay = stream->codecpar->video_delay;
        return delay <= 0 ? 0 : Math.Max(delay, 2);
    }

    private static void CopyOne(
        Demuxer demuxer,
        CopySegment segment,
        Flicks offset,
        Output output,
        Muxer muxer,
        AvPacket owned,
        CancellationToken cancellationToken,
        IProgress<CopyProgress>? progress,
        Flicks total)
    {
        VideoLane? video = output.Video;
        int anchorStream = video?.Source ?? output.Audio[0].Source;

        // Land a little before the start so every stream's first packet is still ahead: an audio
        // packet at the keyframe's own time can sit before the keyframe in the file.
        Flicks margin = Flicks.FromMilliseconds(1000);
        demuxer.SeekToKeyframeBefore(anchorStream, segment.Start > margin ? segment.Start - margin : Flicks.Zero);

        // Half a frame either way, because a millisecond container rounds a keyframe's time and
        // the planner's times come from the same packets through the same rounding.
        Flicks slack = video?.FrameRate is { } fps ? Flicks.FromFrames(1, fps) / 2 : Flicks.FromMilliseconds(1);

        video?.BeginSegment();
        foreach (AudioLane lane in output.Audio)
        {
            lane.BeginSegment();
        }

        bool videoStarted = video is null;
        bool videoDone = video is null;
        Flicks reported = Flicks.Zero;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AVPacket* read = demuxer.ReadPacket(-1);
            if (read is null)
            {
                break;
            }

            int index = read->stream_index;
            AVStream* stream = demuxer.GetStream(index);
            var timeBase = new Rational(stream->time_base.num, stream->time_base.den);
            long stamp = read->pts != ffmpeg.AV_NOPTS_VALUE ? read->pts : read->dts;
            if (stamp == ffmpeg.AV_NOPTS_VALUE)
            {
                continue;
            }

            Flicks at = Flicks.FromTimebase(stamp, timeBase);
            bool key = (read->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

            if (video is not null && index == video.Source)
            {
                if (!videoStarted)
                {
                    if (!key || at < segment.Start - slack)
                    {
                        continue;
                    }

                    videoStarted = true;
                }
                else if (key && at >= segment.End - slack)
                {
                    videoDone = true;
                }

                if (videoDone)
                {
                    if (AllAudioPast(output, segment.End))
                    {
                        break;
                    }

                    continue;
                }

                // An open group's leading pictures refer back past the start; they are left out.
                if (at < segment.Start - slack)
                {
                    continue;
                }

                MovePacket(read, owned);
                video.Write(owned.Handle, at - segment.Start + offset, muxer);
                output.VideoPackets++;

                Flicks done = offset + (at - segment.Start);
                if (progress is not null && done - reported > Flicks.FromMilliseconds(250))
                {
                    reported = done;
                    progress.Report(new CopyProgress(done, total, muxer.BytesWritten));
                }

                continue;
            }

            AudioLane? audio = output.AudioFor(index);
            if (audio is null)
            {
                continue;
            }

            if (at >= segment.End)
            {
                audio.Past = true;
                if (videoDone && AllAudioPast(output, segment.End))
                {
                    break;
                }

                continue;
            }

            if (at < segment.Start)
            {
                continue;
            }

            int samples = ffmpeg.av_get_audio_frame_duration2(stream->codecpar, read->size);
            if (samples <= 0 && read->duration > 0)
            {
                samples = (int)ffmpeg.av_rescale_q(read->duration, stream->time_base, new AVRational { num = 1, den = audio.SampleRate });
            }

            MovePacket(read, owned);
            audio.Write(owned.Handle, at - segment.Start + offset, samples, muxer);
            output.AudioPackets++;
        }

        video?.EndSegment();
    }

    private static bool AllAudioPast(Output output, Flicks end)
    {
        foreach (AudioLane lane in output.Audio)
        {
            if (!lane.Past)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Moves the demuxer's packet into one the muxer may take, leaving the demuxer's empty.</summary>
    private static void MovePacket(AVPacket* from, AvPacket to)
    {
        to.Unref();
        ffmpeg.av_packet_move_ref(to.Handle, from);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException error)
        {
            Log.Warning(error, "Could not delete the partial copy {Path}", path);
        }
        catch (UnauthorizedAccessException error)
        {
            Log.Warning(error, "Could not delete the partial copy {Path}", path);
        }
    }

    private sealed class Output
    {
        public VideoLane? Video { get; set; }

        public List<AudioLane> Audio { get; } = [];

        public long VideoPackets { get; set; }

        public long AudioPackets { get; set; }

        public AudioLane? AudioFor(int source)
        {
            foreach (AudioLane lane in Audio)
            {
                if (lane.Source == source)
                {
                    return lane;
                }
            }

            return null;
        }
    }

    /// <summary>One video stream's timestamps across the joins.</summary>
    private sealed class VideoLane(int source, int output, Rational timeBase, int reorder, Rational? frameRate)
    {
        private readonly PriorityQueue<long, long> _pending = new();
        private long _count;
        private long _firstPts;
        private long _lastDts = long.MinValue;

        public int Source { get; } = source;

        public Rational? FrameRate { get; } = frameRate;

        public void BeginSegment()
        {
            _pending.Clear();
            _count = 0;
        }

        public void EndSegment()
        {
        }

        public void Write(AVPacket* packet, Flicks outputTime, Muxer muxer)
        {
            long pts = outputTime.ToTimebase(timeBase.Num, timeBase.Den, RoundingMode.Nearest);
            long step = FrameRate is null ? Math.Max(1, packet->duration) : 1;

            if (_count == 0)
            {
                _firstPts = pts;
            }

            _pending.Enqueue(pts, pts);

            // The k-th packet decodes when the (k - d)-th earliest picture is due; the first d
            // packets decode a frame apart before the first picture.
            long dts = _count >= reorder
                ? _pending.Dequeue()
                : _firstPts - ((reorder - _count) * step);

            if (dts > pts)
            {
                dts = pts;
            }

            if (dts <= _lastDts)
            {
                dts = _lastDts + 1;
            }

            if (dts > pts)
            {
                throw new FfmpegException(
                    $"The video of stream {Source} reorders more deeply than its header says; the copy cannot give it valid decode times.");
            }

            _lastDts = dts;
            _count++;

            packet->pts = pts;
            packet->dts = dts;
            packet->duration = FrameRate is null ? packet->duration : 1;
            muxer.Write(packet, output, timeBase);
        }
    }

    /// <summary>One audio stream's sample count across the joins.</summary>
    private sealed class AudioLane(int source, int output, int sampleRate)
    {
        private long _next = long.MinValue;
        private long _end;

        public int Source { get; } = source;

        public int SampleRate { get; } = sampleRate;

        public bool Past { get; set; }

        public void BeginSegment()
        {
            Past = false;
            _next = long.MinValue;
        }

        public void Write(AVPacket* packet, Flicks outputTime, int samples, Muxer muxer)
        {
            if (_next == long.MinValue)
            {
                // The first packet of a stretch goes where its source time puts it, unless that
                // overlaps the sound already written.
                long placed = outputTime.ToTimebase(1, SampleRate, RoundingMode.Nearest);
                _next = Math.Max(placed, _end);
            }

            packet->pts = _next;
            packet->dts = _next;
            packet->duration = samples;
            _next += samples;
            _end = _next;
            muxer.Write(packet, output, new Rational(1, SampleRate));
        }
    }
}
