using JazzHands.Core.Time;
using Serilog;

namespace JazzHands.Media.Decode;

/// <summary>How precisely a seek should land.</summary>
public enum SeekMode
{
    /// <summary>
    /// The frame actually displayed at the requested time. Seeks to the keyframe before it and
    /// decodes forward, which is the only way to be frame accurate.
    /// </summary>
    Exact,

    /// <summary>
    /// The nearest keyframe at or before the requested time. Instant, and what shuttling and
    /// coarse scrubbing use before settling.
    /// </summary>
    Nearest,
}

/// <summary>
/// Frame-accurate seeking over a <see cref="Demuxer"/> and <see cref="VideoDecoder"/> pair.
/// </summary>
/// <remarks>
/// Seeking in a compressed stream lands on a keyframe, not on the frame you asked for. Getting
/// the right frame means seeking backwards to the enclosing keyframe, flushing the decoder, and
/// decoding forward while throwing frames away. This class does that, and avoids it when the
/// target is only a little ahead of where the decoder already is, which is the common case while
/// playing or nudging the playhead.
/// </remarks>
public sealed class Seeker : IVideoSource
{
    /// <summary>
    /// How far back the first retry steps when a seek overshoots its target. Eight frames clears
    /// any sane reorder delay in one go, and the step doubles after that so a pathological file
    /// still reaches the start of the stream in a handful of attempts.
    /// </summary>
    private const int InitialBackoffFrames = 8;

    private readonly ILogger _log = Log.ForContext<Seeker>();
    private readonly Demuxer _demuxer;
    private readonly VideoDecoder _decoder;
    private readonly int _streamIndex;
    private readonly Rational _frameRate;
    private readonly bool _ownsResources;

    private VideoFrame? _pending;
    private long _currentFrameIndex = -1;
    private bool _disposed;

    /// <summary>Wraps an existing demuxer and decoder. Neither is owned.</summary>
    public Seeker(Demuxer demuxer, VideoDecoder decoder, int streamIndex)
    {
        ArgumentNullException.ThrowIfNull(demuxer);
        ArgumentNullException.ThrowIfNull(decoder);

        _demuxer = demuxer;
        _decoder = decoder;
        _streamIndex = streamIndex;
        _frameRate = decoder.FrameRate;
        _ownsResources = false;
    }

    private Seeker(Demuxer demuxer, VideoDecoder decoder, int streamIndex, bool ownsResources)
        : this(demuxer, decoder, streamIndex) =>
        _ownsResources = ownsResources;

    /// <summary>
    /// How far ahead of the decoder a target can be before seeking beats decoding forward. Two
    /// thirds of a second at 60 fps is about forty frames, which decode faster than a seek plus a
    /// GOP of catch-up on any codec in the corpus.
    /// </summary>
    public int MaxForwardDecodeFrames { get; set; } = 40;

    /// <summary>Frames decoded and discarded while landing on targets. A rough cost counter.</summary>
    public long FramesDiscarded { get; private set; }

    /// <summary>Seeks performed. Compare with <see cref="FramesDiscarded"/> when tuning.</summary>
    public long Seeks { get; private set; }

    /// <summary>Opens a file and builds a seeker over its best video stream.</summary>
    public static Seeker Open(string path, HardwareDeviceContext? hardware = null, int poolDepth = 4)
    {
        var demuxer = new Demuxer(path);
        try
        {
            int stream = demuxer.FindBestStream(Probe.StreamKind.Video);
            if (stream < 0)
            {
                throw new InvalidOperationException($"'{path}' has no video stream.");
            }

            var decoder = new VideoDecoder(demuxer, stream, hardware, poolDepth);
            return new Seeker(demuxer, decoder, stream, ownsResources: true);
        }
        catch
        {
            demuxer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns the frame displayed at <paramref name="target"/>, or null when the target is past
    /// the end of the stream. The caller disposes the frame.
    /// </summary>
    public VideoFrame? Seek(Flicks target, SeekMode mode = SeekMode.Exact)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A new seek makes any frame held by Flush stale, and this method reads through ReadNext,
        // which would otherwise hand that stale frame back as the seek's result.
        DropPending();

        long targetFrame = target.ToFrames(_frameRate, RoundingMode.Floor);
        if (targetFrame < 0)
        {
            targetFrame = 0;
        }

        if (mode == SeekMode.Nearest)
        {
            SeekAndFlush(target);
            return ReadNext();
        }

        // Decoding forward is cheaper than a seek when the target is just ahead, which is what
        // playback and single-frame stepping look like.
        bool canDecodeForward = _currentFrameIndex >= 0
            && targetFrame > _currentFrameIndex
            && targetFrame - _currentFrameIndex <= MaxForwardDecodeFrames;

        return canDecodeForward ? ScanForwardTo(targetFrame) : SeekBackThenScan(target, targetFrame);
    }

    /// <summary>
    /// The frame at <paramref name="resumeAt"/>, held back so the next read returns it.
    /// </summary>
    /// <remarks>
    /// This is what makes a seeker an <see cref="IVideoSource"/> and so lets the conform stages
    /// sit on top of one. A stage flushes its own state and then pulls, and what it pulls has to
    /// be the frame at the position it flushed to; holding the frame here is how a seek and a
    /// read stay one operation from the stage's point of view.
    /// </remarks>
    public void Flush(Flicks resumeAt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DropPending();
        _pending = Seek(resumeAt);
    }

    /// <inheritdoc />
    VideoFrame? IVideoSource.ReadFrame() => ReadNext();

    /// <summary>Throws away a frame held by <see cref="Flush"/>, if there is one.</summary>
    private void DropPending()
    {
        _pending?.Dispose();
        _pending = null;
    }

    /// <summary>Reads the next frame in order, keeping the seeker's position in step.</summary>
    public VideoFrame? ReadNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pending is not null)
        {
            VideoFrame held = _pending;
            _pending = null;
            return held;
        }

        VideoFrame? frame = _decoder.ReadFrame();
        if (frame is not null)
        {
            _currentFrameIndex = frame.Pts.ToFrames(_frameRate, RoundingMode.Nearest);
        }

        return frame;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DropPending();


        if (_ownsResources)
        {
            _decoder.Dispose();
            _demuxer.Dispose();
        }
    }

    /// <summary>
    /// Seeks to a keyframe at or before <paramref name="target"/> and decodes forward to
    /// <paramref name="targetFrame"/>, stepping the search backwards if the seek overshoots.
    /// </summary>
    /// <remarks>
    /// A container's seek index is keyed on decode timestamps, but a frame is identified by its
    /// presentation timestamp, and with B-frames the two differ by the reorder delay. Asking
    /// libavformat for the keyframe before presentation time t can therefore land on a keyframe
    /// that presents after t: on a 60 fps HEVC file with a reorder delay of two frames, the
    /// keyframe shown at frame 240 is indexed at decode timestamp 238, so seeking to frame 238
    /// lands on it and the first picture out of the decoder is already past the target. Rather
    /// than guess the delay, which open GOPs and edit lists make unreliable, check where the seek
    /// actually landed and step back until it is at or before the frame that was asked for.
    /// </remarks>
    private VideoFrame? SeekBackThenScan(Flicks target, long targetFrame)
    {
        Flicks searchFrom = target;
        Flicks backoff = Flicks.FromFrames(InitialBackoffFrames, _frameRate);

        while (true)
        {
            SeekAndFlush(searchFrom);

            VideoFrame? first = ReadNext();
            if (first is null)
            {
                return null;
            }

            long index = first.Pts.ToFrames(_frameRate, RoundingMode.Nearest);
            if (index == targetFrame)
            {
                return first;
            }

            if (index < targetFrame)
            {
                first.Dispose();
                FramesDiscarded++;
                return ScanForwardTo(targetFrame);
            }

            // The seek overshot. If there is nothing earlier to try then the stream simply has no
            // picture at or before the target, and the first one is the closest honest answer.
            if (searchFrom.IsZero)
            {
                return first;
            }

            first.Dispose();
            FramesDiscarded++;

            searchFrom = backoff < searchFrom ? searchFrom - backoff : Flicks.Zero;
            backoff += backoff;
        }
    }

    /// <summary>Decodes in order until the frame at <paramref name="targetFrame"/> comes out.</summary>
    private VideoFrame? ScanForwardTo(long targetFrame)
    {
        while (true)
        {
            VideoFrame? frame = ReadNext();
            if (frame is null)
            {
                return null;
            }

            if (frame.Pts.ToFrames(_frameRate, RoundingMode.Nearest) >= targetFrame)
            {
                return frame;
            }

            frame.Dispose();
            FramesDiscarded++;
        }
    }

    private void SeekAndFlush(Flicks target)
    {
        _demuxer.SeekToKeyframeBefore(_streamIndex, target);
        _decoder.Flush();
        _currentFrameIndex = -1;
        Seeks++;
    }
}
