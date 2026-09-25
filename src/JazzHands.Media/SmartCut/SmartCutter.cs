using System.Diagnostics;
using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Audio;
using JazzHands.Media.Decode;
using JazzHands.Media.Encode;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Media.SmartCut;

/// <summary>
/// One piece of a smart cut, in source time: copied packet for packet, or decoded and encoded again.
/// </summary>
/// <param name="Start">The first frame shown, inclusive. A copy starts on a keyframe.</param>
/// <param name="End">The first frame not shown.</param>
/// <param name="Encode">True to decode and encode again; false to copy packets.</param>
/// <param name="From">For an encode, the keyframe decoding starts at. For a copy, the keyframe it stops reading at in decode order, or null to read to the end.</param>
public sealed record SmartSegment(Flicks Start, Flicks End, bool Encode, Flicks? From = null)
{
    /// <summary>How long the piece plays.</summary>
    public Flicks Duration => End - Start;
}

/// <summary>A smart cut: one source, the pieces of its picture, and the stretches of its sound.</summary>
/// <param name="OutputPath">Where to write.</param>
/// <param name="SourcePath">The source file.</param>
/// <param name="VideoStream">Its video stream.</param>
/// <param name="Segments">The picture's pieces, back to back.</param>
/// <param name="AudioStreams">The sound streams to carry, in order.</param>
/// <param name="Ranges">The stretches of the source the export plays, in source time, which the sound follows exactly.</param>
/// <param name="FrameRate">The picture's constant rate.</param>
/// <param name="Encoders">The matched encoders to try, in order.</param>
/// <param name="Container">The muxer, or null to choose from the extension.</param>
/// <param name="FastStart">Put an MP4's index at the front.</param>
/// <param name="Extras">Subtitles and chapters.</param>
public sealed record SmartCutJob(
    string OutputPath,
    string SourcePath,
    int VideoStream,
    IReadOnlyList<SmartSegment> Segments,
    IReadOnlyList<int> AudioStreams,
    IReadOnlyList<TimeRange> Ranges,
    Rational FrameRate,
    IReadOnlyList<string> Encoders,
    string? Container = null,
    bool FastStart = true,
    MuxExtras? Extras = null);

/// <summary>What a smart cut wrote.</summary>
/// <param name="Path">The file.</param>
/// <param name="Bytes">Its size.</param>
/// <param name="Duration">How long it plays.</param>
/// <param name="CopiedPackets">Picture packets copied.</param>
/// <param name="EncodedFrames">Pictures encoded again.</param>
/// <param name="Encoder">The matched encoder, or null when nothing needed encoding.</param>
/// <param name="Notes">Encoders skipped, sound re-encoded.</param>
/// <param name="Elapsed">Wall clock.</param>
public sealed record SmartCutResult(string Path, long Bytes, Flicks Duration, long CopiedPackets, long EncodedFrames, string? Encoder, IReadOnlyList<string> Notes, TimeSpan Elapsed);

/// <summary>
/// Joins copied groups of pictures and re-encoded frames around the cuts into one file.
/// </summary>
/// <remarks>
/// <para>
/// Copied pieces are the source's packets, untouched but for their timestamps and, at the first
/// keyframe after an encoded piece, the source's parameter sets in front of it: an encoded piece
/// carries the encoder's own in band, and the copied frames after it must be decoded with the
/// source's again. After an encoded HEVC piece that a copy follows comes an end of sequence NAL
/// unit, so the source's CRA starts a new coded sequence and may switch parameter sets.
/// </para>
/// <para>
/// Decode times run across the joins without starting again: the k-th packet in decode order
/// decodes when the (k - D)-th earliest picture is shown, where D is the source's reordering and
/// the matched encoder never reorders more. Every piece's pictures come after the last piece's,
/// so that is monotonic and never after a presentation. Each is asserted as it is written.
/// </para>
/// <para>
/// Sound follows the stretches exactly, to the sample: PCM is cut inside its packets, anything
/// else is decoded and encoded again across the whole track with the same encoder, so a cut in
/// the sound is where the cut in the picture is. It is written in step with the picture, so the
/// muxer never holds more than a moment of either.
/// </para>
/// </remarks>
public static unsafe class SmartCutter
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(SmartCutter));

    /// <summary>Describes a source stream for an encoder to match.</summary>
    /// <param name="path">The file.</param>
    /// <param name="stream">Its video stream.</param>
    /// <param name="gopFrames">Frames between keyframes, from the keyframe index.</param>
    /// <returns>The description, or null and why a smart cut cannot take it.</returns>
    public static (MatchSource? Source, string? Reason) Describe(string path, int stream, int gopFrames)
    {
        using var demuxer = new Demuxer(path);
        AVStream* video = demuxer.GetStream(stream);
        AVCodecParameters* parameters = video->codecpar;
        string codec = ffmpeg.avcodec_get_name(parameters->codec_id) ?? "unknown";
        if (codec is not ("h264" or "hevc" or "av1"))
        {
            return (null, $"Smart cut re-encodes H.264, HEVC and AV1; this picture is {codec}.");
        }

        if (parameters->field_order is not (AVFieldOrder.AV_FIELD_PROGRESSIVE or AVFieldOrder.AV_FIELD_UNKNOWN))
        {
            return (null, "The picture is interlaced, and smart cut only joins progressive frames.");
        }

        Rational rate = demuxer.GetFrameRate(stream);
        if (rate.IsZero || rate.Num <= 0)
        {
            return (null, "The picture has no constant frame rate to cut on.");
        }

        var extradata = new ReadOnlySpan<byte>(parameters->extradata, parameters->extradata_size);
        DecoderConfiguration configuration = ParameterSets.Read(codec, extradata);
        if (configuration.Signature is not { } signature)
        {
            return (null, "The source's parameter sets could not be read, so a matched encoder could not be checked against them.");
        }

        int delay = parameters->video_delay;
        var source = new MatchSource(
            codec,
            parameters->width,
            parameters->height,
            (AVPixelFormat)parameters->format,
            rate,
            parameters->profile,
            parameters->level,
            delay <= 0 ? 0 : Math.Max(delay, 2),
            Math.Max(1, gopFrames),
            parameters->bit_rate,
            (parameters->color_primaries, parameters->color_trc, parameters->color_space, parameters->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED ? AVColorRange.AVCOL_RANGE_MPEG : parameters->color_range, parameters->chroma_location),
            signature);
        return (source, null);
    }

    /// <summary>Runs a smart cut.</summary>
    /// <param name="job">What to write.</param>
    /// <param name="source">The source's description, from <see cref="Describe"/>.</param>
    /// <param name="progress">Told how far it has got; may be null.</param>
    /// <param name="cancellationToken">Stops it. The partial file is deleted.</param>
    public static SmartCutResult Run(SmartCutJob job, MatchSource source, IProgress<CopyProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(source);
        if (job.Segments.Count == 0)
        {
            throw new ArgumentException("There is nothing to cut.", nameof(job));
        }

        var clock = Stopwatch.StartNew();
        var notes = new List<string>();
        Flicks total = Flicks.Zero;
        foreach (SmartSegment segment in job.Segments)
        {
            total += segment.Duration;
        }

        Muxer? muxer = null;
        var copyDemuxer = new Demuxer(job.SourcePath);
        copyDemuxer.Keep(job.VideoStream);
        var lanes = new List<SoundLane>();
        bool complete = false;
        string? encoderName = null;
        long encodedFrames = 0;
        long copiedPackets = 0;

        try
        {
            muxer = Muxer.Create(job.OutputPath, job.Container);
            AVStream* sourceVideo = copyDemuxer.GetStream(job.VideoStream);
            string codec = source.Codec;
            DecoderConfiguration configuration = ParameterSets.Read(codec, new ReadOnlySpan<byte>(sourceVideo->codecpar->extradata, sourceVideo->codecpar->extradata_size));
            var timeBase = new Rational(job.FrameRate.Den, job.FrameRate.Num);
            int videoOut = muxer.AddCopiedStream(sourceVideo->codecpar, timeBase, Av.ReadDictionary(sourceVideo->metadata));

            foreach (int stream in job.AudioStreams)
            {
                lanes.Add(SoundLane.Open(job.SourcePath, stream, job.Ranges, muxer, notes));
            }

            job.Extras?.Open(muxer);
            muxer.WriteHeader(job.FastStart);

            var timestamps = new Timestamps(source.ReorderDepth, muxer, videoOut, timeBase);
            byte[] parameterSets = codec == "av1" ? [.. configuration.Units.SelectMany(unit => unit)] : ParameterSets.LengthPrefixed(configuration.Units, configuration.LengthSize);

            Flicks offset = Flicks.Zero;
            bool encodedBefore = false;
            for (int index = 0; index < job.Segments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SmartSegment segment = job.Segments[index];
                bool copyFollows = index + 1 < job.Segments.Count && !job.Segments[index + 1].Encode;

                void Advance(Flicks outputTime)
                {
                    foreach (SoundLane lane in lanes)
                    {
                        lane.WriteUpTo(outputTime, muxer!);
                    }

                    job.Extras?.WriteUpTo(muxer!, outputTime);
                    progress?.Report(new CopyProgress(outputTime, total, muxer!.BytesWritten));
                }

                if (segment.Encode)
                {
                    (string name, long frames) = EncodeSegment(job, source, segment, offset, timestamps, configuration.LengthSize, codec == "hevc" && copyFollows, notes, Advance, cancellationToken);
                    encoderName = name;
                    encodedFrames += frames;
                    encodedBefore = true;
                }
                else
                {
                    copiedPackets += CopySegment(copyDemuxer, job, segment, offset, timestamps, encodedBefore ? parameterSets : null, Advance, cancellationToken);
                    encodedBefore = false;
                }

                offset += segment.Duration;
            }

            foreach (SoundLane lane in lanes)
            {
                lane.Finish(muxer);
            }

            job.Extras?.Close(muxer);
            muxer.Finish();
            complete = true;

            long bytes = new FileInfo(muxer.Path).Length;
            progress?.Report(new CopyProgress(total, total, bytes));
            Log.Information(
                "Smart cut {Segments} pieces into {Path}: {Copied} packets copied, {Encoded} frames encoded with {Encoder}, {Bytes} bytes in {Elapsed} ms",
                job.Segments.Count,
                muxer.Path,
                copiedPackets,
                encodedFrames,
                encoderName ?? "none",
                bytes,
                clock.ElapsedMilliseconds);

            return new SmartCutResult(muxer.Path, bytes, total, copiedPackets, encodedFrames, encoderName, notes, clock.Elapsed);
        }
        finally
        {
            string? path = muxer?.Path;
            muxer?.Dispose();
            copyDemuxer.Dispose();
            foreach (SoundLane lane in lanes)
            {
                lane.Dispose();
            }

            if (!complete && path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static long CopySegment(
        Demuxer demuxer,
        SmartCutJob job,
        SmartSegment segment,
        Flicks offset,
        Timestamps timestamps,
        byte[]? parameterSets,
        Action<Flicks> advance,
        CancellationToken cancellationToken)
    {
        Flicks margin = Flicks.FromMilliseconds(1000);
        demuxer.SeekToKeyframeBefore(job.VideoStream, segment.Start > margin ? segment.Start - margin : Flicks.Zero);
        Flicks slack = Flicks.FromFrames(1, job.FrameRate) / 2;
        Rational sourceBase = demuxer.GetTimeBase(job.VideoStream);
        using var owned = new AvPacket();
        bool started = false;
        long copied = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AVPacket* read = demuxer.ReadPacket(job.VideoStream);
            if (read is null)
            {
                break;
            }

            long stamp = read->pts != ffmpeg.AV_NOPTS_VALUE ? read->pts : read->dts;
            if (stamp == ffmpeg.AV_NOPTS_VALUE)
            {
                continue;
            }

            Flicks at = Flicks.FromTimebase(stamp, sourceBase);
            bool key = (read->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
            if (!started)
            {
                if (!key || at < segment.Start - slack)
                {
                    continue;
                }

                started = true;
            }
            else if (key && segment.From is { } stop && at >= stop - slack)
            {
                break;
            }

            // Leading pictures of the first keyframe, shown before the piece starts, and anything
            // past its end, belong to the pieces either side.
            if (at < segment.Start - slack || at >= segment.End - slack)
            {
                continue;
            }

            ffmpeg.av_packet_unref(owned.Handle);
            if (copied == 0 && parameterSets is not null)
            {
                // The source's parameter sets back in band, in front of the first keyframe.
                Av.Check(ffmpeg.av_new_packet(owned.Handle, parameterSets.Length + read->size), "av_new_packet");
                parameterSets.CopyTo(new Span<byte>(owned.Handle->data, parameterSets.Length));
                new ReadOnlySpan<byte>(read->data, read->size).CopyTo(new Span<byte>(owned.Handle->data + parameterSets.Length, read->size));
                Av.Check(ffmpeg.av_packet_copy_props(owned.Handle, read), "av_packet_copy_props");
            }
            else
            {
                ffmpeg.av_packet_move_ref(owned.Handle, read);
            }

            Flicks outputTime = at - segment.Start + offset;
            long frame = outputTime.ToFrames(job.FrameRate, RoundingMode.Nearest);
            timestamps.Write(owned.Handle, frame);
            copied++;
            advance(outputTime);
        }

        return copied;
    }

    private static (string Encoder, long Frames) EncodeSegment(
        SmartCutJob job,
        MatchSource source,
        SmartSegment segment,
        Flicks offset,
        Timestamps timestamps,
        int lengthSize,
        bool endSequence,
        List<string> notes,
        Action<Flicks> advance,
        CancellationToken cancellationToken)
    {
        using var demuxer = new Demuxer(job.SourcePath);
        demuxer.Keep(job.VideoStream);
        using var decoder = new VideoDecoder(demuxer, job.VideoStream, hardware: null);
        demuxer.SeekToKeyframeBefore(job.VideoStream, segment.From ?? segment.Start);
        decoder.Flush();

        var skipped = new List<string>();
        using MatchedEncoder encoder = MatchedEncoder.Open(source, job.Encoders, globalHeader: false, skipped);
        foreach (string note in skipped.Select(reason => $"Skipped {reason}."))
        {
            if (!notes.Contains(note))
            {
                notes.Add(note);
            }
        }

        Flicks slack = Flicks.FromFrames(1, job.FrameRate) / 2;
        long firstFrame = offset.ToFrames(job.FrameRate, RoundingMode.Nearest);
        long frames = 0;
        byte[]? held = null;
        long heldFrame = 0;
        bool heldKey = false;

        void Write(byte[] data, long frame, bool key)
        {
            using var packet = new AvPacket();
            Av.Check(ffmpeg.av_new_packet(packet.Handle, data.Length), "av_new_packet");
            data.CopyTo(new Span<byte>(packet.Handle->data, data.Length));
            if (key)
            {
                packet.Handle->flags |= ffmpeg.AV_PKT_FLAG_KEY;
            }

            timestamps.Write(packet.Handle, frame);
        }

        void Take(nint raw)
        {
            var packet = (AVPacket*)raw;
            var data = new ReadOnlySpan<byte>(packet->data, packet->size);
            byte[] converted = source.Codec is "h264" or "hevc" && ParameterSets.IsAnnexB(data)
                ? ParameterSets.AnnexBToLengthPrefixed(data, lengthSize)
                : data.ToArray();

            // Held one back, so the last of the piece can end its sequence.
            if (held is not null)
            {
                Write(held, heldFrame, heldKey);
            }

            held = converted;
            heldFrame = firstFrame + packet->pts;
            heldKey = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VideoFrame? frame = decoder.ReadFrame();
            if (frame is null)
            {
                break;
            }

            using (frame)
            {
                if (frame.Pts < segment.Start - slack)
                {
                    continue;
                }

                if (frame.Pts >= segment.End - slack)
                {
                    break;
                }

                long index = (frame.Pts - segment.Start).ToFrames(job.FrameRate, RoundingMode.Nearest);
                encoder.Send(frame.Handle, index, Take);
                frames++;
                advance(offset + Flicks.FromFrames(index, job.FrameRate));
            }
        }

        encoder.Flush(Take);
        if (held is not null)
        {
            if (endSequence)
            {
                held = [.. held, .. ParameterSets.LengthPrefixed([ParameterSets.HevcEndOfSequence.ToArray()], lengthSize)];
            }

            Write(held, heldFrame, heldKey);
        }

        long expected = segment.Duration.ToFrames(job.FrameRate, RoundingMode.Nearest);
        if (frames != expected)
        {
            throw new FfmpegException($"Re-encoding {Timecode.FormatClock(segment.Start)} to {Timecode.FormatClock(segment.End)} decoded {frames} frames where {expected} were due.");
        }

        return (encoder.Name, frames);
    }

    /// <summary>Picture timestamps across the pieces: presentation from the frame, decode times derived and asserted.</summary>
    private sealed class Timestamps(int reorder, Muxer muxer, int stream, Rational timeBase)
    {
        private readonly PriorityQueue<long, long> _pending = new();
        private long _count;
        private long _first;
        private long _lastDts = long.MinValue;
        private long _lastPts = long.MinValue;

        public void Write(AVPacket* packet, long frame)
        {
            if (_count == 0)
            {
                _first = frame;
            }

            _pending.Enqueue(frame, frame);
            long dts = _count >= reorder ? _pending.Dequeue() : _first - (reorder - _count);
            if (dts <= _lastDts || dts > frame)
            {
                throw new FfmpegException($"The joined picture's decode times went wrong at frame {frame}: {dts} after {_lastDts}.");
            }

            if (frame == _lastPts)
            {
                throw new FfmpegException($"Two pictures were given frame {frame}.");
            }

            _lastDts = dts;
            _lastPts = Math.Max(_lastPts, frame);
            _count++;
            packet->pts = frame;
            packet->dts = dts;
            packet->duration = 1;
            packet->pos = -1;
            muxer.Write(packet, stream, timeBase);
        }
    }

    /// <summary>One sound stream of the output, following the stretches to the sample.</summary>
    private sealed class SoundLane : IDisposable
    {
        private readonly Demuxer _demuxer;
        private readonly int _stream;
        private readonly int _output;
        private readonly int _rate;
        private readonly (long Start, long End)[] _stretches;
        private readonly AudioDecoder? _decoder;
        private readonly AudioEncoder? _encoder;
        private readonly int _frameBytes;
        private float[][] _planes = [];
        private int _stretch;
        private long _written;
        private long _position = long.MinValue;
        private bool _done;

        private SoundLane(Demuxer demuxer, int stream, int output, int rate, (long, long)[] stretches, AudioDecoder? decoder, AudioEncoder? encoder, int frameBytes)
        {
            _demuxer = demuxer;
            _stream = stream;
            _output = output;
            _rate = rate;
            _stretches = stretches;
            _decoder = decoder;
            _encoder = encoder;
            _frameBytes = frameBytes;
        }

        public static SoundLane Open(string path, int stream, IReadOnlyList<TimeRange> ranges, Muxer muxer, List<string> notes)
        {
            var demuxer = new Demuxer(path);
            try
            {
                demuxer.Keep(stream);
                AVStream* source = demuxer.GetStream(stream);
                AVCodecParameters* parameters = source->codecpar;
                int rate = parameters->sample_rate;
                (long, long)[] stretches = [.. ranges.Select(range => (
                    range.Start.ToTimebase(1, rate, RoundingMode.Nearest),
                    range.End.ToTimebase(1, rate, RoundingMode.Nearest)))];

                string codec = ffmpeg.avcodec_get_name(parameters->codec_id) ?? "unknown";
                if (codec.StartsWith("pcm_", StringComparison.Ordinal))
                {
                    int frameBytes = ffmpeg.av_get_bits_per_sample(parameters->codec_id) / 8 * parameters->ch_layout.nb_channels;
                    int output = muxer.AddCopiedStream(parameters, new Rational(1, rate), Av.ReadDictionary(source->metadata));
                    return new SoundLane(demuxer, stream, output, rate, stretches, null, null, frameBytes);
                }

                // Everything else is encoded again, whole, with the encoder that made it.
                string encoderName = codec switch
                {
                    "opus" => "libopus",
                    "mp3" => "libmp3lame",
                    string other => other,
                };

                var decoder = new AudioDecoder(demuxer, stream);
                long bitrate = parameters->bit_rate > 0 ? parameters->bit_rate : 0;
                AudioEncoder encoder = AudioEncoder.Open(new AudioEncoderSettings(encoderName, decoder.SampleRate, decoder.Channels, bitrate), muxer.NeedsGlobalHeader);
                int encoded = muxer.AddStream(encoder, Av.ReadDictionary(source->metadata));
                notes.Add($"Sound stream {stream} ({codec}) encoded again across the whole export, so it cuts where the picture does.");
                return new SoundLane(demuxer, stream, encoded, decoder.SampleRate, stretches, decoder, encoder, 0);
            }
            catch
            {
                demuxer.Dispose();
                throw;
            }
        }

        /// <summary>Writes the sound up to an output time.</summary>
        public void WriteUpTo(Flicks outputTime, Muxer muxer)
        {
            long target = outputTime.ToTimebase(1, _rate, RoundingMode.Nearest);
            if (_encoder is not null)
            {
                Encode(target, muxer);
            }
            else
            {
                Trim(target, muxer);
            }
        }

        /// <summary>Writes the rest and drains the encoder.</summary>
        public void Finish(Muxer muxer)
        {
            WriteUpTo(Flicks.FromSeconds(1e6), muxer);
            _encoder?.Flush(muxer, _output);
        }

        public void Dispose()
        {
            _decoder?.Dispose();
            _encoder?.Dispose();
            _demuxer.Dispose();
        }

        private void Encode(long target, Muxer muxer)
        {
            while (!_done && _written < target && _stretch < _stretches.Length)
            {
                (long start, long end) = _stretches[_stretch];
                if (_position == long.MinValue)
                {
                    _decoder!.SeekTo(Flicks.FromSamples(start, _rate));
                    _position = start;
                }

                AudioFrame? frame = _decoder!.ReadFrame();
                if (frame is null)
                {
                    NextStretch();
                    continue;
                }

                using (frame)
                {
                    long first = frame.Pts.ToTimebase(1, _rate, RoundingMode.Nearest);
                    long from = Math.Max(first, _position);
                    long to = Math.Min(first + frame.Frames, end);
                    if (to > from)
                    {
                        int count = (int)(to - from);
                        int skip = (int)(from - first);
                        if (_planes.Length != frame.Channels || _planes[0].Length < count)
                        {
                            _planes = [.. Enumerable.Range(0, frame.Channels).Select(_ => new float[Math.Max(count, 4096)])];
                        }

                        for (int channel = 0; channel < frame.Channels; channel++)
                        {
                            frame.Plane(channel).Slice(skip, count).CopyTo(_planes[channel]);
                        }

                        _encoder!.Write(_planes, 0, count, muxer, _output);
                        _written += count;
                        _position = to;
                    }

                    if (first + frame.Frames >= end)
                    {
                        NextStretch();
                    }
                }
            }
        }

        private void Trim(long target, Muxer muxer)
        {
            using var packet = new AvPacket();
            var timeBase = new Rational(_demuxer.GetTimeBase(_stream).Num, _demuxer.GetTimeBase(_stream).Den);
            while (!_done && _written < target && _stretch < _stretches.Length)
            {
                (long start, long end) = _stretches[_stretch];
                if (_position == long.MinValue)
                {
                    _demuxer.SeekToKeyframeBefore(_stream, Flicks.FromSamples(start, _rate));
                    _position = start;
                }

                AVPacket* read = _demuxer.ReadPacket(_stream);
                if (read is null)
                {
                    NextStretch();
                    continue;
                }

                long stamp = read->pts != ffmpeg.AV_NOPTS_VALUE ? read->pts : read->dts;
                long first = Flicks.FromTimebase(stamp, timeBase).ToTimebase(1, _rate, RoundingMode.Nearest);
                long samples = read->size / _frameBytes;
                long from = Math.Max(first, _position);
                long to = Math.Min(first + samples, end);
                if (to > from)
                {
                    // PCM is its samples: the bytes of the ones in the stretch, cut out exactly.
                    int bytes = (int)(to - from) * _frameBytes;
                    ffmpeg.av_packet_unref(packet.Handle);
                    Av.Check(ffmpeg.av_new_packet(packet.Handle, bytes), "av_new_packet");
                    new ReadOnlySpan<byte>(read->data + ((from - first) * _frameBytes), bytes).CopyTo(new Span<byte>(packet.Handle->data, bytes));
                    packet.Handle->pts = _written;
                    packet.Handle->dts = _written;
                    packet.Handle->duration = to - from;
                    packet.Handle->flags |= ffmpeg.AV_PKT_FLAG_KEY;
                    muxer.Write(packet.Handle, _output, new Rational(1, _rate));
                    _written += to - from;
                    _position = to;
                }

                if (first + samples >= end)
                {
                    NextStretch();
                }
            }
        }

        private void NextStretch()
        {
            _stretch++;
            _position = long.MinValue;
            _done = _stretch >= _stretches.Length;
        }
    }
}
