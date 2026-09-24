using System.Globalization;
using JazzHands.Media.Import;
using JazzHands.Media.Waveforms;

namespace JazzHands.Engine.Caching;

/// <summary>
/// Waveform peaks of media, from memory, then the disk cache, then a read on a background worker.
/// </summary>
/// <remarks>
/// A stream is read once, start to end, and snapshots are published as it goes, so a long file
/// fills in from the left while the rest is read. The finished peaks go to the disk cache; a
/// stream read again in a later session loads in milliseconds.
///
/// Kept in memory up to <see cref="MemoryBudgetBytes"/>: ten minutes of one stream is 1.2 MB, so
/// the budget holds some fifty hours.
/// </remarks>
public sealed class WaveformService : IDisposable
{
    /// <summary>What peaks in memory may add up to.</summary>
    public const long MemoryBudgetBytes = 256L * 1024 * 1024;

    private static readonly TimeSpan SnapshotEvery = TimeSpan.FromMilliseconds(250);

    private readonly CacheManager _cache;
    private readonly WorkQueue _work;
    private readonly bool _ownsWork;
    private readonly MemoryLru<(string Hash, int Stream), AudioPeaks> _memory =
        new(MemoryBudgetBytes, peaks => Math.Max(1, peaks.Windows * 2L));

    /// <summary>A service over a cache, doing its work on a queue.</summary>
    public WaveformService(CacheManager cache, WorkQueue? work = null)
    {
        ArgumentNullException.ThrowIfNull(cache);

        _cache = cache;
        _ownsWork = work is null;
        _work = work ?? new WorkQueue(2, "Waveforms");
    }

    /// <summary>Raised on a worker thread when more of a waveform is ready, with its media's hash.</summary>
    public event EventHandler<string>? Ready;

    /// <summary>The queue the work runs on.</summary>
    public WorkQueue Work => _work;

    /// <summary>
    /// A stream's peaks as far as they are read, or null when nothing is yet. Anything short of
    /// complete is queued.
    /// </summary>
    public AudioPeaks? Get(CacheSource source, WorkPriority priority)
    {
        ArgumentNullException.ThrowIfNull(source);

        _memory.TryGet((source.Hash, source.StreamIndex), out AudioPeaks? peaks);

        if (peaks is not { IsComplete: true })
        {
            string key = string.Create(CultureInfo.InvariantCulture, $"w|{source.Hash}|{source.StreamIndex}");
            _work.Enqueue(key, priority, (_, cancel) => Read(source, cancel));
        }

        return peaks;
    }

    /// <summary>Drops a hash's peaks from memory and its waiting work.</summary>
    public void Forget(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        _memory.RemoveWhere(key => string.Equals(key.Hash, hash, StringComparison.Ordinal));
        _work.Cancel(key => key.StartsWith("w|" + hash + "|", StringComparison.Ordinal));
    }

    /// <summary>Drops everything in memory.</summary>
    public void Clear()
    {
        _memory.Clear();
        _work.Cancel(key => key.StartsWith("w|", StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsWork)
        {
            _work.Dispose();
        }
    }

    private void Read(CacheSource source, CancellationToken cancel)
    {
        var key = (source.Hash, source.StreamIndex);

        if (_memory.TryGet(key, out AudioPeaks? known) && known.IsComplete)
        {
            return;
        }

        if (_cache.GetWaveform(source.Hash, source.StreamIndex) is { } stored)
        {
            Publish(key, AudioPeaks.FromBytes(stored));
            return;
        }

        AudioPeaks peaks = PeakExtractor.Extract(
            source.Path,
            source.StreamIndex,
            source.Duration,
            snapshot => Publish(key, snapshot),
            SnapshotEvery,
            cancel);

        _cache.PutWaveform(source.Hash, source.StreamIndex, peaks.ToBytes());
        Publish(key, peaks);
    }

    private void Publish((string Hash, int Stream) key, AudioPeaks peaks)
    {
        _memory.Set(key, peaks);
        Ready?.Invoke(this, key.Hash);
    }
}
