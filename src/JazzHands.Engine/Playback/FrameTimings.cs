using JazzHands.Core.Commands;

namespace JazzHands.Engine.Playback;

/// <summary>The stages of a played frame that <see cref="FrameTimings"/> times.</summary>
public enum FrameStage
{
    /// <summary>How late the render began.</summary>
    Late,

    /// <summary>Fetching the layers' pictures inside the render.</summary>
    Fetch,

    /// <summary>The whole render.</summary>
    Render,

    /// <summary>The present to the targets.</summary>
    Present,

    /// <summary>Decoding ahead.</summary>
    Ahead,
}

/// <summary>
/// Histograms of each stage of a played frame, filled by the composition thread without
/// allocating and read by anyone: 20 microsecond buckets up to 200 ms, and a last bucket for
/// everything longer.
/// </summary>
public sealed class FrameTimings
{
    private const double BucketMilliseconds = 0.02;
    private const int Buckets = 10_000;
    private static readonly int StageCount = Enum.GetValues<FrameStage>().Length;

    private readonly int[][] _counts;
    private readonly double[] _totals;
    private readonly double[] _longest;
    private long _frames;
    private long _decodedInRender;
    private long _decodedAhead;

    /// <summary>Creates empty histograms.</summary>
    public FrameTimings()
    {
        _counts = new int[StageCount][];
        for (int stage = 0; stage < StageCount; stage++)
        {
            _counts[stage] = new int[Buckets + 1];
        }

        _totals = new double[StageCount];
        _longest = new double[StageCount];
    }

    /// <summary>Records one stage of one frame.</summary>
    public void Add(FrameStage stage, double milliseconds)
    {
        int index = (int)stage;
        int bucket = milliseconds <= 0 ? 0 : (int)Math.Min(Buckets, milliseconds / BucketMilliseconds);
        Interlocked.Increment(ref _counts[index][bucket]);

        // One writer: the composition thread. Readers may see a total a frame behind its counts.
        Volatile.Write(ref _totals[index], _totals[index] + milliseconds);
        if (milliseconds > _longest[index])
        {
            Volatile.Write(ref _longest[index], milliseconds);
        }
    }

    /// <summary>Counts a rendered frame and the pictures it and the decoding after it took.</summary>
    public void Frame(long decodedInRender, long decodedAhead)
    {
        Interlocked.Increment(ref _frames);
        Interlocked.Add(ref _decodedInRender, decodedInRender);
        Interlocked.Add(ref _decodedAhead, decodedAhead);
    }

    /// <summary>Empties every histogram.</summary>
    public void Reset()
    {
        for (int stage = 0; stage < StageCount; stage++)
        {
            Array.Clear(_counts[stage]);
            _totals[stage] = 0;
            _longest[stage] = 0;
        }

        Interlocked.Exchange(ref _frames, 0);
        Interlocked.Exchange(ref _decodedInRender, 0);
        Interlocked.Exchange(ref _decodedAhead, 0);
    }

    /// <summary>What the histograms hold now.</summary>
    public FrameTimingsInfo Snapshot() => new(
        Interlocked.Read(ref _frames),
        Stage(FrameStage.Late),
        Stage(FrameStage.Fetch),
        Stage(FrameStage.Render),
        Stage(FrameStage.Present),
        Stage(FrameStage.Ahead),
        Interlocked.Read(ref _decodedInRender),
        Interlocked.Read(ref _decodedAhead));

    private StageTiming Stage(FrameStage stage)
    {
        int index = (int)stage;
        int[] counts = _counts[index];
        long total = 0;
        for (int bucket = 0; bucket <= Buckets; bucket++)
        {
            total += Volatile.Read(ref counts[bucket]);
        }

        if (total == 0)
        {
            return default;
        }

        return new StageTiming(
            Percentile(counts, total, 0.50),
            Percentile(counts, total, 0.99),
            Math.Round(Volatile.Read(ref _longest[index]), 2),
            Math.Round(Volatile.Read(ref _totals[index]) / total, 2));
    }

    private static double Percentile(int[] counts, long total, double fraction)
    {
        long wanted = (long)Math.Ceiling(total * fraction);
        long seen = 0;
        for (int bucket = 0; bucket <= Buckets; bucket++)
        {
            seen += Volatile.Read(ref counts[bucket]);
            if (seen >= wanted)
            {
                // The middle of the bucket.
                return Math.Round((bucket + 0.5) * BucketMilliseconds, 2);
            }
        }

        return Math.Round(Buckets * BucketMilliseconds, 2);
    }
}
