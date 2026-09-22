using System.Diagnostics;
using System.Globalization;

namespace JazzHands.Core.Time;

/// <summary>
/// A position or duration on the timeline, measured in flicks: 1/705,600,000 of a second.
/// One flick divides exactly into every frame rate Jazz Hands supports (24, 25, 30, 48, 50, 60,
/// 90, 100, 120 and their 1001 variants) and every audio rate (44.1k, 48k, 96k, 192k), so frame
/// and sample boundaries are integers and never drift.
/// </summary>
/// <remarks>
/// The domain model, commands, cache keys and the project file store Flicks only. Doubles in
/// seconds appear at the UI, CLI and FFmpeg filter boundary and nowhere else.
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly record struct Flicks(long Value) : IComparable<Flicks>
{
    /// <summary>Flicks in one second.</summary>
    public const long PerSecond = 705_600_000L;

    /// <summary>Flicks in one millisecond.</summary>
    public const long PerMillisecond = 705_600L;

    /// <summary>Time zero.</summary>
    public static readonly Flicks Zero = new(0);

    /// <summary>The smallest representable position.</summary>
    public static readonly Flicks MinValue = new(long.MinValue);

    /// <summary>The largest representable position.</summary>
    public static readonly Flicks MaxValue = new(long.MaxValue);

    /// <summary>One flick.</summary>
    public static readonly Flicks Epsilon = new(1);

    /// <summary>One second.</summary>
    public static readonly Flicks OneSecond = new(PerSecond);

    /// <summary>True when the value is negative.</summary>
    public bool IsNegative => Value < 0;

    /// <summary>True when the value is exactly zero.</summary>
    public bool IsZero => Value == 0;

    /// <summary>The magnitude, discarding sign.</summary>
    public Flicks Abs => new(Math.Abs(Value));

    /// <summary>
    /// Converts from seconds. Use at UI and CLI boundaries only; engine code converts from
    /// frames, samples or an FFmpeg timebase so the result is exact.
    /// </summary>
    public static Flicks FromSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Seconds must be a finite number.");
        }

        double flicks = Math.Round(seconds * PerSecond, MidpointRounding.AwayFromZero);
        if (flicks is < long.MinValue or > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "The position does not fit in flicks.");
        }

        return new Flicks((long)flicks);
    }

    /// <summary>Converts from whole milliseconds. Exact.</summary>
    public static Flicks FromMilliseconds(long milliseconds) => new(checked(milliseconds * PerMillisecond));

    /// <summary>
    /// Converts a frame index at a frame rate to its exact start position. Exact for every
    /// standard rate; throws when the rate does not divide the flick grid evenly.
    /// </summary>
    public static Flicks FromFrames(long frames, Rational fps)
    {
        if (fps.IsZero || fps.Num < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fps), fps.ToString(), "The frame rate must be positive.");
        }

        Int128 numerator = (Int128)frames * PerSecond * fps.Den;
        if (numerator % fps.Num != 0)
        {
            throw new ArgumentException(
                $"Frame rate {fps} does not divide the flick grid evenly, so frame {frames} has no exact position.",
                nameof(fps));
        }

        return FromBig(numerator / fps.Num);
    }

    /// <summary>Converts a sample index at a sample rate to its exact position. Exact for all standard rates.</summary>
    public static Flicks FromSamples(long samples, int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "The sample rate must be positive.");
        }

        Int128 numerator = (Int128)samples * PerSecond;
        if (numerator % sampleRate != 0)
        {
            throw new ArgumentException(
                $"Sample rate {sampleRate} does not divide the flick grid evenly, so sample {samples} has no exact position.",
                nameof(sampleRate));
        }

        return FromBig(numerator / sampleRate);
    }

    /// <summary>
    /// Converts an FFmpeg timestamp in the given timebase. Computed in 128-bit so a 1/90000
    /// timebase near the end of a long file cannot overflow.
    /// </summary>
    public static Flicks FromTimebase(long pts, long timebaseNum, long timebaseDen)
    {
        if (timebaseDen <= 0 || timebaseNum <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timebaseDen), "The timebase must be positive.");
        }

        Int128 numerator = (Int128)pts * PerSecond * timebaseNum;
        return FromBig(Divide(numerator, timebaseDen, RoundingMode.Nearest));
    }

    /// <summary>Converts an FFmpeg timestamp in the given timebase.</summary>
    public static Flicks FromTimebase(long pts, Rational timebase) => FromTimebase(pts, timebase.Num, timebase.Den);

    /// <summary>The smaller of two positions.</summary>
    public static Flicks Min(Flicks a, Flicks b) => a.Value <= b.Value ? a : b;

    /// <summary>The larger of two positions.</summary>
    public static Flicks Max(Flicks a, Flicks b) => a.Value >= b.Value ? a : b;

    /// <summary>Constrains a position to a range. The bounds are inclusive.</summary>
    public static Flicks Clamp(Flicks value, Flicks min, Flicks max) => min > max
        ? throw new ArgumentOutOfRangeException(nameof(min), "The lower bound is above the upper bound.")
        : Min(Max(value, min), max);

    public static Flicks operator +(Flicks a, Flicks b) => new(checked(a.Value + b.Value));

    public static Flicks operator -(Flicks a, Flicks b) => new(checked(a.Value - b.Value));

    public static Flicks operator -(Flicks a) => new(checked(-a.Value));

    public static Flicks operator *(Flicks a, long factor) => new(checked(a.Value * factor));

    public static Flicks operator *(long factor, Flicks a) => a * factor;

    public static Flicks operator /(Flicks a, long divisor) => new(a.Value / divisor);

    /// <summary>How many times <paramref name="b"/> fits in <paramref name="a"/>, truncated.</summary>
    public static long operator /(Flicks a, Flicks b) => a.Value / b.Value;

    public static Flicks operator %(Flicks a, Flicks b) => new(a.Value % b.Value);

    public static bool operator <(Flicks a, Flicks b) => a.Value < b.Value;

    public static bool operator <=(Flicks a, Flicks b) => a.Value <= b.Value;

    public static bool operator >(Flicks a, Flicks b) => a.Value > b.Value;

    public static bool operator >=(Flicks a, Flicks b) => a.Value >= b.Value;

    /// <summary>Named alternate for the addition operator.</summary>
    public static Flicks Add(Flicks a, Flicks b) => a + b;

    /// <summary>Named alternate for the subtraction operator.</summary>
    public static Flicks Subtract(Flicks a, Flicks b) => a - b;

    /// <summary>Named alternate for the multiplication operator.</summary>
    public static Flicks Multiply(Flicks a, long factor) => a * factor;

    /// <summary>Named alternate for the division operator.</summary>
    public static Flicks Divide(Flicks a, long divisor) => a / divisor;

    /// <summary>Named alternate for the remainder operator.</summary>
    public static Flicks Remainder(Flicks a, Flicks b) => a % b;

    /// <summary>Named alternate for the unary negation operator.</summary>
    public static Flicks Negate(Flicks a) => -a;

    /// <summary>
    /// The frame index at this position. Pass <see cref="RoundingMode.Floor"/> for the frame on
    /// screen at t, <see cref="RoundingMode.Nearest"/> to snap user input, and
    /// <see cref="RoundingMode.Ceiling"/> for the first frame at or after t.
    /// </summary>
    public long ToFrames(Rational fps, RoundingMode rounding)
    {
        if (fps.IsZero || fps.Num < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fps), fps.ToString(), "The frame rate must be positive.");
        }

        Int128 numerator = (Int128)Value * fps.Num;
        Int128 denominator = (Int128)PerSecond * fps.Den;
        return (long)Divide(numerator, denominator, rounding);
    }

    /// <summary>The sample index at this position.</summary>
    public long ToSamples(int sampleRate, RoundingMode rounding)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "The sample rate must be positive.");
        }

        return (long)Divide((Int128)Value * sampleRate, PerSecond, rounding);
    }

    /// <summary>This position as an FFmpeg timestamp in the given timebase.</summary>
    public long ToTimebase(long timebaseNum, long timebaseDen, RoundingMode rounding)
    {
        if (timebaseDen <= 0 || timebaseNum <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timebaseDen), "The timebase must be positive.");
        }

        Int128 numerator = (Int128)Value * timebaseDen;
        Int128 denominator = (Int128)PerSecond * timebaseNum;
        return (long)Divide(numerator, denominator, rounding);
    }

    /// <summary>This position as an FFmpeg timestamp in the given timebase.</summary>
    public long ToTimebase(Rational timebase, RoundingMode rounding) =>
        ToTimebase(timebase.Num, timebase.Den, rounding);

    /// <summary>Seconds as a double. Display, logging and FFmpeg filter strings only.</summary>
    public double ToSeconds() => (double)Value / PerSecond;

    /// <summary>Milliseconds as a double. Display only.</summary>
    public double ToMilliseconds() => (double)Value / PerMillisecond;

    /// <summary>Snaps this position down to the start of the frame that contains it.</summary>
    public Flicks SnapToFrame(Rational fps, RoundingMode rounding = RoundingMode.Floor) =>
        FromFrames(ToFrames(fps, rounding), fps);

    /// <inheritdoc />
    public int CompareTo(Flicks other) => Value.CompareTo(other.Value);

    /// <summary>Renders as a raw flick count with the "fl" suffix. Use Timecode for anything a person reads.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Value}fl");

    private static Flicks FromBig(Int128 value) => value < long.MinValue || value > long.MaxValue
        ? throw new OverflowException("The position does not fit in flicks.")
        : new Flicks((long)value);

    private static Int128 Divide(Int128 numerator, Int128 denominator, RoundingMode rounding)
    {
        Int128 quotient = numerator / denominator;
        Int128 remainder = numerator - (quotient * denominator);
        if (remainder == 0)
        {
            return quotient;
        }

        bool negative = (numerator < 0) ^ (denominator < 0);
        switch (rounding)
        {
            case RoundingMode.Truncate:
                return quotient;

            case RoundingMode.Floor:
                return negative ? quotient - 1 : quotient;

            case RoundingMode.Ceiling:
                return negative ? quotient : quotient + 1;

            case RoundingMode.Nearest:
                Int128 twice = (remainder < 0 ? -remainder : remainder) * 2;
                Int128 magnitude = denominator < 0 ? -denominator : denominator;
                if (twice < magnitude)
                {
                    return quotient;
                }

                return negative ? quotient - 1 : quotient + 1;

            default:
                throw new ArgumentOutOfRangeException(nameof(rounding), rounding, "Unknown rounding mode.");
        }
    }
}
