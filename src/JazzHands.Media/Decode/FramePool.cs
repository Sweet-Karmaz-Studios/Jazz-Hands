using System.Collections.Concurrent;
using JazzHands.Media.Interop;

namespace JazzHands.Media.Decode;

/// <summary>
/// Recycles <c>AVFrame</c> shells so steady-state decoding allocates nothing on the managed heap.
/// </summary>
/// <remarks>
/// The pixels are never in the shell: a hardware frame points at a texture array slice, a software
/// frame at FFmpeg's own buffers, and both are released by <c>av_frame_unref</c>. What the pool
/// saves is the per-frame allocation of the shell and its wrapper, which at 4K60 across several
/// decoders is the difference between a quiet heap and a gen0 collection every second.
///
/// Thread safety: rent and return are safe from any thread, but a given decoder is thread-affine
/// and should only be driven from its own thread.
/// </remarks>
public sealed class FramePool : IDisposable
{
    private readonly ConcurrentBag<AvFrame> _frames = [];
    private readonly int _capacity;
    private int _count;
    private bool _disposed;

    /// <summary>Creates a pool that keeps at most <paramref name="capacity"/> frame shells.</summary>
    public FramePool(int capacity = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>Frame shells currently held for reuse.</summary>
    public int Available => _frames.Count;

    /// <summary>Frame shells ever allocated. Flat in steady state is the point.</summary>
    public int Allocated => _count;

    /// <summary>Takes a cleared frame shell from the pool, allocating only when the pool is empty.</summary>
    public AvFrame Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_frames.TryTake(out AvFrame? frame))
        {
            return frame;
        }

        Interlocked.Increment(ref _count);
        return new AvFrame();
    }

    /// <summary>Returns a frame shell, releasing whatever buffers it referenced.</summary>
    public void Return(AvFrame frame)
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

    /// <inheritdoc />
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
