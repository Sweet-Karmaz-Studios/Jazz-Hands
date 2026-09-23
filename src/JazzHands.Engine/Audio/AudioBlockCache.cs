using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace JazzHands.Engine.Audio;

/// <summary>Names one block of decoded audio.</summary>
/// <param name="Hash">The media's content hash, so a replaced file is never served from the old one's blocks.</param>
/// <param name="Stream">The stream within the file.</param>
/// <param name="SampleRate">The rate it was converted to, because the same stream at two rates is two different sets of samples.</param>
/// <param name="Block">Which block: sample <c>Block * BlockFrames</c> onwards.</param>
public readonly record struct AudioBlockKey(string Hash, int Stream, int SampleRate, long Block);

/// <summary>
/// A block of decoded samples in managed arrays, owned by the cache.
/// </summary>
/// <remarks>
/// Copied out of the decoder rather than holding its frame. A block is a few kilobytes, so the
/// copy costs less than reference counting AVFrames the way the video frame cache must, and a
/// managed array can be read on the audio thread with nothing to release afterwards. Blocks are
/// never recycled: one evicted while the audio thread is still reading it simply stays alive
/// until the read is done.
/// </remarks>
public sealed class AudioBlock
{
    private readonly float[][] _planes;
    private long _lastUsed;

    /// <summary>Wraps planes the caller hands over.</summary>
    public AudioBlock(float[][] planes)
    {
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentOutOfRangeException.ThrowIfZero(planes.Length);

        _planes = planes;
        Frames = planes[0].Length;
    }

    /// <summary>How many channels.</summary>
    public int Channels => _planes.Length;

    /// <summary>How many samples per channel.</summary>
    public int Frames { get; }

    /// <summary>What it costs the budget.</summary>
    public long Bytes => (long)Channels * Frames * sizeof(float);

    internal long LastUsed
    {
        get => Volatile.Read(ref _lastUsed);
        set => Volatile.Write(ref _lastUsed, value);
    }

    /// <summary>One channel's samples.</summary>
    public ReadOnlySpan<float> Plane(int channel) => _planes[channel];
}

/// <summary>
/// Decoded audio by (media, stream, rate, block), least recently used out first, inside a budget.
/// </summary>
/// <remarks>
/// Read from the audio thread and written from the decode thread at once, so lookups go through
/// a <see cref="ConcurrentDictionary{TKey, TValue}"/>, whose reads take no lock and allocate
/// nothing. Recency is a counter bumped with an interlocked increment, not a linked list, because
/// a list would need a lock on every read. Eviction is the writer's job: it happens in
/// <see cref="Add"/>, sorts a snapshot by recency, and is never done by a reader.
///
/// The default budget holds about eleven minutes of stereo at 48 kHz, which is thirty seconds
/// each for twenty clips with room to spare. A block is never evicted by its own add, for the
/// same reason a frame never is: the caller is about to read it.
/// </remarks>
public sealed class AudioBlockCache
{
    /// <summary>Samples per block per channel. About 43 ms at 48 kHz.</summary>
    public const int BlockFrames = 2048;

    /// <summary>128 MB.</summary>
    public const long DefaultBudgetBytes = 128L * 1024 * 1024;

    private readonly ConcurrentDictionary<AudioBlockKey, AudioBlock> _blocks = new();
    private long _clock;
    private long _bytes;
    private long _hits;
    private long _misses;
    private long _evicted;

    /// <summary>Creates a cache.</summary>
    public AudioBlockCache(long budgetBytes = DefaultBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        BudgetBytes = budgetBytes;
    }

    /// <summary>How much it may hold.</summary>
    public long BudgetBytes { get; }

    /// <summary>How much it holds.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>How many blocks it holds.</summary>
    public int Count => _blocks.Count;

    /// <summary>Lookups that found their block.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Lookups that did not.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Blocks dropped to stay inside the budget.</summary>
    public long Evicted => Interlocked.Read(ref _evicted);

    /// <summary>Looks a block up and marks it recently used. Safe on the audio thread.</summary>
    public bool TryGet(in AudioBlockKey key, [NotNullWhen(true)] out AudioBlock? block)
    {
        if (_blocks.TryGetValue(key, out block))
        {
            block.LastUsed = Interlocked.Increment(ref _clock);
            Interlocked.Increment(ref _hits);
            return true;
        }

        Interlocked.Increment(ref _misses);
        return false;
    }

    /// <summary>True when a block is present, without counting as a use.</summary>
    public bool Contains(in AudioBlockKey key) => _blocks.ContainsKey(key);

    /// <summary>Adds a block, replacing any with the same key, and evicts down to the budget.</summary>
    public void Add(in AudioBlockKey key, AudioBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        block.LastUsed = Interlocked.Increment(ref _clock);

        AudioBlock? replaced = null;
        _blocks.AddOrUpdate(
            key,
            block,
            (_, old) =>
            {
                replaced = old;
                return block;
            });

        Interlocked.Add(ref _bytes, block.Bytes - (replaced?.Bytes ?? 0));

        if (Bytes > BudgetBytes)
        {
            Evict(key);
        }
    }

    /// <summary>Drops everything, for a change of mix rate or a cache clear.</summary>
    public void Clear()
    {
        _blocks.Clear();
        Interlocked.Exchange(ref _bytes, 0);
    }

    /// <summary>Drops the least recently used blocks until the cache is a tenth under budget.</summary>
    private void Evict(AudioBlockKey keep)
    {
        long target = BudgetBytes - (BudgetBytes / 10);
        KeyValuePair<AudioBlockKey, AudioBlock>[] entries = [.. _blocks];
        Array.Sort(entries, static (a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));

        foreach ((AudioBlockKey key, AudioBlock block) in entries)
        {
            if (Bytes <= target)
            {
                break;
            }

            if (key == keep)
            {
                continue;
            }

            if (_blocks.TryRemove(new KeyValuePair<AudioBlockKey, AudioBlock>(key, block)))
            {
                Interlocked.Add(ref _bytes, -block.Bytes);
                Interlocked.Increment(ref _evicted);
            }
        }
    }
}
