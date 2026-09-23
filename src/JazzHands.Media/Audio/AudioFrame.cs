using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Interop;

namespace JazzHands.Media.Audio;

/// <summary>
/// A block of decoded audio, planar float 32, at one sample rate.
/// </summary>
/// <remarks>
/// Planar rather than interleaved because every stage downstream works a channel at a time: a
/// gain is one multiply across a plane, a pan reads two planes and writes two, and an effect that
/// wants interleaved samples is rare enough to convert for itself. Float 32 because the whole
/// graph mixes in float and clipping in the middle of a chain is not recoverable.
///
/// The samples belong to the frame and are released with it, like a video frame. Nothing in the
/// audio path holds one past the block it was pulled for; the block cache copies instead.
/// </remarks>
public sealed unsafe class AudioFrame : IDisposable
{
    private readonly FramePoolAdapter? _pool;
    private AvFrame? _frame;

    internal AudioFrame(AvFrame frame, FramePoolAdapter? pool, Flicks pts, int sampleRate)
    {
        _frame = frame;
        _pool = pool;
        Pts = pts;
        SampleRate = sampleRate;

        AVFrame* raw = frame.Handle;
        Frames = raw->nb_samples;
        Channels = raw->ch_layout.nb_channels;
    }

    /// <summary>Where this block starts on the source timeline.</summary>
    public Flicks Pts { get; internal set; }

    /// <summary>Samples a second.</summary>
    public int SampleRate { get; }

    /// <summary>How many channels there are.</summary>
    public int Channels { get; }

    /// <summary>How many samples each channel holds.</summary>
    public int Frames { get; }

    /// <summary>How long this block lasts.</summary>
    public Flicks Duration => Flicks.FromSamples(Frames, SampleRate);

    /// <summary>True while the samples are still there.</summary>
    public bool IsValid => _frame is not null;

    /// <summary>One channel's samples. No copy.</summary>
    public ReadOnlySpan<float> Plane(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, Channels);

        AVFrame* raw = Handle;
        return new ReadOnlySpan<float>(raw->extended_data[(uint)channel], Frames);
    }

    /// <summary>Returns the frame to its pool. Always dispose: a leaked block starves the decoder.</summary>
    public void Dispose()
    {
        if (_frame is null)
        {
            return;
        }

        AvFrame frame = _frame;
        _frame = null;

        if (_pool is not null)
        {
            _pool.Return(frame);
        }
        else
        {
            frame.Dispose();
        }
    }

    private AVFrame* Handle => _frame is null
        ? throw new ObjectDisposedException(nameof(AudioFrame))
        : _frame.Handle;
}

/// <summary>
/// Recycles AVFrame shells for audio, the way <see cref="Decode.FramePool"/> does for video.
/// </summary>
/// <remarks>
/// A separate type rather than a shared one because an audio frame's buffers are sized by the
/// sample count and a video frame's by the picture, and a pool that mixed them would hand back a
/// shell holding the wrong kind of allocation.
/// </remarks>
internal sealed class FramePoolAdapter : IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentBag<AvFrame> _frames = [];
    private readonly int _capacity;
    private int _count;
    private bool _disposed;

    internal FramePoolAdapter(int capacity = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    internal int Allocated => _count;

    internal AvFrame Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_frames.TryTake(out AvFrame? frame))
        {
            return frame;
        }

        Interlocked.Increment(ref _count);
        return new AvFrame();
    }

    internal void Return(AvFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        frame.Unref();

        if (_disposed || _frames.Count >= _capacity)
        {
            frame.Dispose();
            return;
        }

        _frames.Add(frame);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        while (_frames.TryTake(out AvFrame? frame))
        {
            frame.Dispose();
        }
    }
}
