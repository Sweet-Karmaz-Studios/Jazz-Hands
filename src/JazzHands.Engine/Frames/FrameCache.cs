using JazzHands.Core.Time;
using JazzHands.Render.Frames;
using Serilog;

namespace JazzHands.Engine.Frames;

/// <summary>Which frame, of which stream, of which media.</summary>
/// <param name="Hash">The media's content hash, so a file that moved keeps its frames.</param>
/// <param name="StreamIndex">The video stream.</param>
/// <param name="Pts">The frame's presentation time in the source.</param>
public readonly record struct FrameKey(string Hash, int StreamIndex, Flicks Pts);

/// <summary>
/// Decoded frames, kept in video memory until the budget says otherwise.
/// </summary>
/// <remarks>
/// Scrubbing back and forth over the same few seconds asks for the same frames over and over, and
/// decoding them again each time is what makes a scrub feel heavy. Reverse playback needs a whole
/// group of pictures held at once, because the only way to play a group backwards is to decode it
/// forwards and keep it.
///
/// Frames are held in textures this cache owns rather than the decoder's own surfaces. A decoder
/// allocates a fixed number of those and hands out slices, so keeping eight would stall the
/// decoder that filled them; see <see cref="DecoderFrameCopier"/>.
///
/// Eviction is least recently used, except for what is pinned. Pinning is what keeps the frames
/// around the playhead from being evicted by a background thumbnail sweep that touched a thousand
/// frames once each.
///
/// Thread affine, like everything downstream of a device context.
/// </remarks>
public sealed class FrameCache : IDisposable
{
    /// <summary>What the cache may hold. 1.5 GB by default.</summary>
    public const long DefaultBudgetBytes = 1_610_612_736;

    private readonly ILogger _log = Log.ForContext<FrameCache>();
    private readonly Dictionary<FrameKey, Entry> _entries = [];
    private readonly FrameTexturePool _textures;
    private readonly bool _ownsTextures;
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    private long _ticks;
    private bool _disposed;

    /// <summary>Creates a cache over a texture pool.</summary>
    /// <param name="textures">Where frame textures come from. Owned unless <paramref name="ownsTextures"/> says otherwise.</param>
    /// <param name="budgetBytes">What the cached frames may add up to.</param>
    /// <param name="ownsTextures">False when the pool is shared with something else.</param>
    public FrameCache(FrameTexturePool textures, long budgetBytes = DefaultBudgetBytes, bool ownsTextures = true)
    {
        ArgumentNullException.ThrowIfNull(textures);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);

        _textures = textures;
        _ownsTextures = ownsTextures;
        BudgetBytes = budgetBytes;
    }

    /// <summary>What the cached frames may add up to.</summary>
    public long BudgetBytes { get; }

    /// <summary>What they add up to now.</summary>
    public long Bytes { get; private set; }

    /// <summary>How many frames are cached.</summary>
    public int Count => _entries.Count;

    /// <summary>Frames asked for and found.</summary>
    public long Hits { get; private set; }

    /// <summary>Frames asked for and not there.</summary>
    public long Misses { get; private set; }

    /// <summary>Frames dropped to stay inside the budget.</summary>
    public long Evicted { get; private set; }

    /// <summary>The proportion of requests that were already cached, for the diagnostics.</summary>
    public double HitRate => Hits + Misses == 0 ? 0 : (double)Hits / (Hits + Misses);

    /// <summary>The texture pool, for the code that fills a frame before adding it.</summary>
    public FrameTexturePool Textures => _textures;

    /// <summary>
    /// The cached frame for a key, or null. A hit is moved to the front of the eviction order.
    /// </summary>
    /// <remarks>
    /// The frame belongs to the cache and stays valid until it is evicted, which cannot happen
    /// while the caller is on the same thread and has not asked for anything else. A caller that
    /// wants a longer guarantee pins it.
    /// </remarks>
    public FrameTexture? Get(FrameKey key)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_entries.TryGetValue(key, out Entry? entry))
        {
            entry.LastUsed = ++_ticks;
            Hits++;
            return entry.Frame;
        }

        Misses++;
        return null;
    }

    /// <summary>True when a key is cached, without counting it as a hit or a miss.</summary>
    public bool Contains(FrameKey key) => _entries.ContainsKey(key);

    /// <summary>
    /// Adds a frame, taking ownership of it. Adding a key that is already there replaces it.
    /// </summary>
    public void Add(FrameKey key, FrameTexture frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_entries.TryGetValue(key, out Entry? existing))
        {
            Bytes -= existing.Frame.Bytes;
            existing.Frame.Dispose();
            _entries.Remove(key);
        }

        _entries[key] = new Entry(frame) { LastUsed = ++_ticks };
        Bytes += frame.Bytes;

        TrimToBudget();
    }

    /// <summary>
    /// Keeps the frames in a source time range from being evicted, and unpins everything else of
    /// that stream.
    /// </summary>
    /// <remarks>
    /// The playhead's neighbourhood, in practice. Without it a background sweep that touched a
    /// thousand frames once would push out the twenty the person is actually looking at.
    /// </remarks>
    public void Pin(string hash, int streamIndex, TimeRange range)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach ((FrameKey key, Entry entry) in _entries)
        {
            if (!string.Equals(key.Hash, hash, StringComparison.Ordinal) || key.StreamIndex != streamIndex)
            {
                continue;
            }

            entry.Pinned = key.Pts >= range.Start && key.Pts < range.End;
        }
    }

    /// <summary>Unpins everything, so the budget can reclaim whatever it needs.</summary>
    public void UnpinAll()
    {
        foreach (Entry entry in _entries.Values)
        {
            entry.Pinned = false;
        }
    }

    /// <summary>Drops every frame of one media item, which a reprobe or a removal needs.</summary>
    public void Forget(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        VerifyThread();

        foreach (FrameKey key in _entries.Keys
            .Where(key => string.Equals(key.Hash, hash, StringComparison.Ordinal))
            .ToList())
        {
            Remove(key);
        }
    }

    /// <summary>Drops everything.</summary>
    public void Clear()
    {
        VerifyThread();

        foreach (Entry entry in _entries.Values)
        {
            entry.Frame.Dispose();
        }

        _entries.Clear();
        Bytes = 0;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (Entry entry in _entries.Values)
        {
            entry.Frame.Dispose();
        }

        _entries.Clear();
        Bytes = 0;

        if (_ownsTextures)
        {
            _textures.Dispose();
        }

        _log.Debug(
            "Frame cache closed: {Hits} hits, {Misses} misses, {Evicted} evicted",
            Hits,
            Misses,
            Evicted);
    }

    private void Remove(FrameKey key)
    {
        if (!_entries.Remove(key, out Entry? entry))
        {
            return;
        }

        Bytes -= entry.Frame.Bytes;
        entry.Frame.Dispose();
    }

    private void TrimToBudget()
    {
        while (Bytes > BudgetBytes)
        {
            FrameKey? oldest = null;
            long oldestTick = long.MaxValue;

            foreach ((FrameKey key, Entry entry) in _entries)
            {
                // Never the one just touched. Without this, adding a frame to a cache that is
                // already full evicts the frame that was added, and the caller is handed a
                // texture that has already been released.
                if (entry.Pinned || entry.LastUsed == _ticks || entry.LastUsed >= oldestTick)
                {
                    continue;
                }

                oldest = key;
                oldestTick = entry.LastUsed;
            }

            if (oldest is not { } evict)
            {
                // Everything left is pinned or is the frame that was just asked for. Going over is
                // better than dropping the frames around the playhead, and the next pin or the
                // next add will release them.
                _log.Debug(
                    "Over the frame cache budget at {Megabytes:F0} MB with every frame pinned",
                    Bytes / (1024.0 * 1024));
                return;
            }

            Remove(evict);
            Evicted++;
        }
    }

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId == _threadId)
        {
            return;
        }

        throw new InvalidOperationException(
            $"This frame cache belongs to thread {_threadId} and was used from thread "
            + $"{Environment.CurrentManagedThreadId}. It holds Direct3D textures, which are bound to the "
            + "context that made them.");
    }

    private sealed class Entry(FrameTexture frame)
    {
        internal FrameTexture Frame { get; } = frame;

        internal long LastUsed { get; set; }

        internal bool Pinned { get; set; }
    }
}
