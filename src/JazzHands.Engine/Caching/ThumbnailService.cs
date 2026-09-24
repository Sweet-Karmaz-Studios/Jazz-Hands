using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using JazzHands.Core.Time;
using JazzHands.Media.Import;
using JazzHands.Media.Thumbnails;

namespace JazzHands.Engine.Caching;

/// <summary>A thumbnail as the service hands it out.</summary>
/// <param name="Time">The time it was asked for, on its grid.</param>
/// <param name="FrameTime">The source time of the frame it shows.</param>
/// <param name="Jpeg">The picture.</param>
public sealed record ThumbnailImage(Flicks Time, Flicks FrameTime, byte[] Jpeg);

/// <summary>
/// Thumbnails of media, from memory, then the disk cache, then a decode on a background worker.
/// </summary>
/// <remarks>
/// <para>
/// Asked while drawing, so it answers at once with what it has and queues the rest; <see
/// cref="Ready"/> says when more has arrived. Thumbnails sit on a grid: a strip at a zoom asks for
/// times on the multiples of one of <see cref="Buckets"/>, so neighbouring zooms share most of
/// their pictures and a scroll finds the same times it cached a moment ago.
/// </para>
/// <para>
/// A strip at a coarse spacing does not need exact frames. Each request carries a tolerance of
/// half its spacing, and the extractor takes the keyframe before the time when it is that close:
/// one frame decoded instead of a group's worth. A finer zoom asks again with a smaller tolerance
/// and the picture is taken again exactly; until then the one from the coarse pass is shown,
/// which is better than a placeholder.
/// </para>
/// <para>
/// Work is queued a chunk at a time (up to four seconds of grid) so one worker takes a run of
/// neighbouring times and decodes forward through them, rather than several workers seeking into
/// the same group of pictures. Visible and near work is dropped when nothing has asked for it in
/// half a second; idle work (a whole clip filled in behind the visible part) is kept.
/// </para>
/// </remarks>
public sealed class ThumbnailService : IDisposable
{
    /// <summary>The height every thumbnail is cached at.</summary>
    public const int Height = ThumbnailExtractor.DefaultHeight;

    /// <summary>How many thumbnails are kept in memory: the caching skill's 4000.</summary>
    public const int MemoryCapacity = 4000;

    private static readonly Flicks ChunkLength = Flicks.FromSeconds(4);

    /// <summary>
    /// How far before its time a first, rough picture may be: longer than the keyframe interval
    /// of almost anything recorded (OBS two seconds, phones one, cameras up to five).
    /// </summary>
    private static readonly Flicks CoarseTolerance = Flicks.FromSeconds(5);
    private static readonly long WantedFor = Stopwatch.Frequency / 2;

    private readonly CacheManager _cache;
    private readonly WorkQueue _work;
    private readonly bool _ownsWork;
    private readonly MemoryLru<Key, ThumbnailImage> _memory = new(MemoryCapacity);
    private readonly ConcurrentDictionary<string, long> _asked = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Key, byte> _missing = new();

    /// <summary>A service over a cache, doing its work on a queue.</summary>
    /// <param name="cache">Where thumbnails are kept between sessions.</param>
    /// <param name="work">Where decoding happens; a queue of its own when null.</param>
    /// <param name="priority">The priority of a queue of its own; tests raise it, see <see cref="WorkQueue"/>.</param>
    public ThumbnailService(CacheManager cache, WorkQueue? work = null, ThreadPriority priority = ThreadPriority.BelowNormal)
    {
        ArgumentNullException.ThrowIfNull(cache);

        _cache = cache;
        _ownsWork = work is null;
        _work = work ?? new WorkQueue(WorkQueue.DefaultWorkers, "Thumbnails", priority);
    }

    /// <summary>Raised on a worker thread when a thumbnail is ready, with its media's hash.</summary>
    public event EventHandler<string>? Ready;

    /// <summary>
    /// The spacings a strip is taken at. A strip uses the closest one that leaves a thumbnail's
    /// width between neighbours.
    /// </summary>
    public static ImmutableArray<Flicks> Buckets { get; } =
    [
        Flicks.FromSeconds(0.5),
        Flicks.FromSeconds(1),
        Flicks.FromSeconds(2),
        Flicks.FromSeconds(5),
        Flicks.FromSeconds(10),
        Flicks.FromSeconds(30),
        Flicks.FromSeconds(60),
        Flicks.FromSeconds(300),
    ];

    /// <summary>The queue the work runs on, for tests and the benchmark.</summary>
    public WorkQueue Work => _work;

    /// <summary>Thumbnails in memory.</summary>
    public int InMemory => _memory.Count;

    /// <summary>
    /// The grid spacing for a strip: the smallest bucket whose thumbnails, <paramref name="tileWidth"/>
    /// pixels wide at this zoom, do not overlap.
    /// </summary>
    public static Flicks SpacingFor(double pixelsPerSecond, double tileWidth)
    {
        foreach (Flicks bucket in Buckets)
        {
            if (bucket.ToSeconds() * pixelsPerSecond >= tileWidth)
            {
                return bucket;
            }
        }

        return Buckets[^1];
    }

    /// <summary>How far before its time a thumbnail on a grid may be: half the spacing.</summary>
    public static Flicks ToleranceFor(Flicks spacing) => spacing / 2;

    /// <summary>
    /// The thumbnails of a stretch of a source on a grid, as far as they are ready. The missing
    /// ones are queued.
    /// </summary>
    /// <param name="source">The stream.</param>
    /// <param name="spacing">The grid, one of <see cref="Buckets"/> or any other spacing.</param>
    /// <param name="from">The start of the stretch, in source time.</param>
    /// <param name="to">Its end.</param>
    /// <param name="priority">How soon the missing ones are wanted.</param>
    public IReadOnlyList<ThumbnailImage> Strip(CacheSource source, Flicks spacing, Flicks from, Flicks to, WorkPriority priority)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(spacing.Value, 0L);

        var found = new List<ThumbnailImage>();
        List<Flicks>? missing = null;
        List<Flicks>? rough = null;
        Flicks tolerance = ToleranceFor(spacing);

        foreach (Flicks time in Grid(source, spacing, from, to))
        {
            if (Lookup(source, time, tolerance, out ThumbnailImage? image) is { } good)
            {
                found.Add(good);
                continue;
            }

            if (image is not null)
            {
                // A picture from a coarser pass is shown while the exact one is made.
                found.Add(image);
                (rough ??= []).Add(time);
            }
            else
            {
                (missing ??= []).Add(time);
            }
        }

        if (missing is not null && tolerance < CoarseTolerance)
        {
            // Nothing at all yet: a keyframe first, at the priority asked for, so the strip fills
            // in at keyframe speed; the exact frames follow a step down. At a fine zoom on long
            // groups of pictures the exact frame is dozens of decodes and the keyframe is one.
            Queue(source, spacing, CoarseTolerance, missing, priority);
            Queue(source, spacing, tolerance, missing, Later(priority));
        }
        else if (missing is not null)
        {
            Queue(source, spacing, tolerance, missing, priority);
        }

        if (rough is not null)
        {
            Queue(source, spacing, tolerance, rough, priority);
        }

        return found;
    }

    /// <summary>One thumbnail near a time, for a poster frame or a hover scrub. Queued when it is not ready.</summary>
    /// <param name="source">The stream.</param>
    /// <param name="time">The source time.</param>
    /// <param name="tolerance">How far before the time the picture may be.</param>
    /// <param name="priority">How soon it is wanted.</param>
    /// <returns>The thumbnail, or one further off than asked while a better one is made, or null.</returns>
    public ThumbnailImage? At(CacheSource source, Flicks time, Flicks tolerance, WorkPriority priority)
    {
        ArgumentNullException.ThrowIfNull(source);

        time = Clamp(source, time);
        if (Lookup(source, time, tolerance, out ThumbnailImage? near) is { } good)
        {
            return good;
        }

        Queue(source, tolerance > Flicks.Zero ? tolerance * 2 : Flicks.FromFrames(1, Rational.Fps60), tolerance, [time], priority);
        return near;
    }

    /// <summary>Drops everything in memory about a hash and its waiting work. The disk cache is the cache manager's.</summary>
    public void Forget(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        _memory.RemoveWhere(key => string.Equals(key.Hash, hash, StringComparison.Ordinal));
        foreach (Key key in _missing.Keys.Where(key => string.Equals(key.Hash, hash, StringComparison.Ordinal)))
        {
            _missing.TryRemove(key, out _);
        }

        _work.Cancel(key => key.StartsWith("t|" + hash + "|", StringComparison.Ordinal));
    }

    /// <summary>Drops everything in memory.</summary>
    public void Clear()
    {
        _memory.Clear();
        _missing.Clear();
        _work.Cancel(key => key.StartsWith("t|", StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsWork)
        {
            _work.Dispose();
        }
    }

    /// <summary>The grid times in a stretch, inside the source.</summary>
    private static IEnumerable<Flicks> Grid(CacheSource source, Flicks spacing, Flicks from, Flicks to)
    {
        if (source.Duration <= Flicks.Zero)
        {
            // A still is one picture whatever the zoom.
            yield return Flicks.Zero;
            yield break;
        }

        long first = Math.Max(0, from.Value / spacing.Value);
        long last = (Flicks.Min(to, source.Duration).Value - 1) / spacing.Value;

        for (long index = first; index <= last; index++)
        {
            yield return spacing * index;
        }
    }

    private static Flicks Clamp(CacheSource source, Flicks time) =>
        source.Duration <= Flicks.Zero ? Flicks.Zero
        : time < Flicks.Zero ? Flicks.Zero
        : time >= source.Duration ? Flicks.Max(Flicks.Zero, source.Duration - Flicks.FromFrames(1, Rational.Fps60))
        : time;

    /// <summary>What memory holds for a time: good when within tolerance, otherwise the near miss for show.</summary>
    private ThumbnailImage? Lookup(CacheSource source, Flicks time, Flicks tolerance, out ThumbnailImage? near)
    {
        near = null;

        if (_memory.TryGet(new Key(source.Hash, source.StreamIndex, time.Value), out ThumbnailImage image))
        {
            if (Within(image, time, tolerance))
            {
                return image;
            }

            near = image;
        }

        return null;
    }

    private static WorkPriority Later(WorkPriority priority) =>
        priority == WorkPriority.Visible ? WorkPriority.Near : WorkPriority.Idle;

    private static bool Within(ThumbnailImage image, Flicks time, Flicks tolerance) =>
        (image.FrameTime <= time ? time - image.FrameTime : image.FrameTime - time) <= tolerance;

    private void Queue(CacheSource source, Flicks spacing, Flicks tolerance, List<Flicks> times, WorkPriority priority)
    {
        long now = Stopwatch.GetTimestamp();

        if (_asked.Count > 20_000)
        {
            foreach ((string stale, long asked) in _asked)
            {
                if (now - asked > WantedFor * 10)
                {
                    _asked.TryRemove(stale, out _);
                }
            }
        }

        foreach (IGrouping<long, Flicks> chunk in times
            .Where(time => !_missing.ContainsKey(new Key(source.Hash, source.StreamIndex, time.Value)))
            .GroupBy(time => time.Value / Math.Max(spacing.Value, ChunkLength.Value)))
        {
            string key = string.Create(
                CultureInfo.InvariantCulture,
                $"t|{source.Hash}|{source.StreamIndex}|{spacing.Value}|{tolerance.Value}|{chunk.Key}");

            Flicks[] run = [.. chunk];

            Func<bool>? stillWanted = null;
            if (priority != WorkPriority.Idle)
            {
                _asked[key] = now;
                stillWanted = () => _asked.TryGetValue(key, out long asked) && Stopwatch.GetTimestamp() - asked < WantedFor;
            }

            _work.Enqueue(key, priority, (context, cancel) => Make(context, source, tolerance, run, cancel), stillWanted);
        }
    }

    /// <summary>On a worker: each time from the disk cache if it is there and close enough, else decoded.</summary>
    private void Make(WorkerContext context, CacheSource source, Flicks tolerance, Flicks[] times, CancellationToken cancel)
    {
        ThumbnailExtractor? extractor = null;

        foreach (Flicks time in times)
        {
            cancel.ThrowIfCancellationRequested();

            var key = new Key(source.Hash, source.StreamIndex, time.Value);
            if (_memory.TryGet(key, out ThumbnailImage have) && Within(have, time, tolerance))
            {
                continue;
            }

            if (_cache.GetThumbnail(source.Hash, source.StreamIndex, time, Height) is { } cached
                && Within(new ThumbnailImage(time, cached.FrameTime, cached.Jpeg), time, tolerance))
            {
                Publish(key, new ThumbnailImage(time, cached.FrameTime, cached.Jpeg));
                continue;
            }

            extractor ??= context.Get(
                string.Create(CultureInfo.InvariantCulture, $"thumb|{source.Path}|{source.StreamIndex}"),
                () => new ThumbnailExtractor(source.Path, source.StreamIndex, source.DemuxOptions, Height));

            Thumbnail? thumbnail = extractor.Extract(time, tolerance);
            if (thumbnail is null)
            {
                // Past the end of what the stream really holds, which the probe can overstate by
                // a frame or two. Remembered so it is not asked for again on every draw.
                _missing[key] = 0;
                continue;
            }

            _cache.PutThumbnail(source.Hash, source.StreamIndex, time, Height, thumbnail.FrameTime, thumbnail.Jpeg);
            Publish(key, new ThumbnailImage(time, thumbnail.FrameTime, thumbnail.Jpeg));
        }
    }

    private void Publish(Key key, ThumbnailImage image)
    {
        _memory.Set(key, image);
        Ready?.Invoke(this, key.Hash);
    }

    private readonly record struct Key(string Hash, int Stream, long Time);
}
