using System.Diagnostics;
using System.Globalization;

namespace JazzHands.Core.Time;

/// <summary>
/// A half-open span on the timeline: <see cref="End"/> is exclusive, so two ranges that touch
/// do not overlap and a clip ending at 5s and one starting at 5s are adjacent, not colliding.
/// </summary>
[DebuggerDisplay("{ToString(),nq}")]
public readonly record struct TimeRange : IComparable<TimeRange>
{
    /// <summary>An empty range at time zero.</summary>
    public static readonly TimeRange Empty = new(Flicks.Zero, Flicks.Zero);

    /// <summary>Creates a range. The duration must not be negative.</summary>
    public TimeRange(Flicks start, Flicks duration)
    {
        if (duration.IsNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "A range cannot have a negative duration.");
        }

        Start = start;
        Duration = duration;
    }

    /// <summary>The first position inside the range.</summary>
    public Flicks Start { get; }

    /// <summary>The length of the range. Never negative.</summary>
    public Flicks Duration { get; }

    /// <summary>The first position after the range. Exclusive.</summary>
    public Flicks End => Start + Duration;

    /// <summary>True when the range has zero length.</summary>
    public bool IsEmpty => Duration.IsZero;

    /// <summary>Creates a range from inclusive start and exclusive end positions.</summary>
    public static TimeRange FromBounds(Flicks start, Flicks end) => end < start
        ? throw new ArgumentOutOfRangeException(nameof(end), end, "The end of a range cannot precede its start.")
        : new TimeRange(start, end - start);

    /// <summary>Creates a range covering whole frames at the given rate.</summary>
    public static TimeRange FromFrames(long startFrame, long frameCount, Rational fps) => frameCount < 0
        ? throw new ArgumentOutOfRangeException(nameof(frameCount), frameCount, "A range cannot cover a negative number of frames.")
        : FromBounds(Flicks.FromFrames(startFrame, fps), Flicks.FromFrames(startFrame + frameCount, fps));

    /// <summary>True when the position falls inside the half-open range.</summary>
    public bool Contains(Flicks position) => position >= Start && position < End;

    /// <summary>True when the other range lies entirely inside this one.</summary>
    public bool Contains(TimeRange other) => other.Start >= Start && other.End <= End;

    /// <summary>True when the two ranges share at least one flick. Touching ranges do not intersect.</summary>
    public bool Intersects(TimeRange other) => Start < other.End && other.Start < End;

    /// <summary>The overlapping part of the two ranges, or an empty range at the later start when they do not overlap.</summary>
    public TimeRange Intersect(TimeRange other)
    {
        Flicks start = Flicks.Max(Start, other.Start);
        Flicks end = Flicks.Min(End, other.End);
        return end <= start ? new TimeRange(start, Flicks.Zero) : FromBounds(start, end);
    }

    /// <summary>The smallest range covering both, including any gap between them.</summary>
    public TimeRange Union(TimeRange other) =>
        FromBounds(Flicks.Min(Start, other.Start), Flicks.Max(End, other.End));

    /// <summary>The same length, moved by an offset.</summary>
    public TimeRange Shift(Flicks offset) => new(Start + offset, Duration);

    /// <summary>The same start, with a different length.</summary>
    public TimeRange WithDuration(Flicks duration) => new(Start, duration);

    /// <summary>The same length, at a different start.</summary>
    public TimeRange WithStart(Flicks start) => new(start, Duration);

    /// <summary>The number of whole frames the range covers at the given rate.</summary>
    public long FrameCount(Rational fps) => End.ToFrames(fps, RoundingMode.Ceiling) - Start.ToFrames(fps, RoundingMode.Floor);

    /// <summary>Orders by start, then by duration, which is the order clips appear on a track.</summary>
    public int CompareTo(TimeRange other)
    {
        int byStart = Start.CompareTo(other.Start);
        return byStart != 0 ? byStart : Duration.CompareTo(other.Duration);
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{Start.Value}fl, {End.Value}fl)");
}
