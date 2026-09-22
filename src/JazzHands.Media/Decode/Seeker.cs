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
public sealed class Seeker : IDisposable
{
    private readonly ILogger _log = Log.ForContext<Seeker>();
    private readonly Demuxer _demuxer;
    private readonly VideoDecoder _decoder;
    private readonly int _streamIndex;
    private readonly Rational _frameRate;
    private readonly bool _ownsResources;

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

        if (!canDecodeForward)
        {
            SeekAndFlush(target);
        }

        while (true)
        {
            VideoFrame? frame = ReadNext();
            if (frame is null)
            {
                return null;
            }

            long index = frame.Pts.ToFrames(_frameRate, RoundingMode.Nearest);
            if (index >= targetFrame)
            {
                return frame;
            }

            frame.Dispose();
            FramesDiscarded++;
        }
    }

    /// <summary>Reads the next frame in order, keeping the seeker's position in step.</summary>
    public VideoFrame? ReadNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

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
        if (_ownsResources)
        {
            _decoder.Dispose();
            _demuxer.Dispose();
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
