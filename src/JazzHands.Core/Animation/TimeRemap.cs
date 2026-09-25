using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Animation;

/// <summary>
/// Time remapping: a clip whose speed is a curve rather than one rate. The source time shown at a
/// moment of the clip is how much source the speed curve has run through by then, its integral.
/// </summary>
/// <remarks>
/// <para>
/// The curve is the clip's <c>remap</c> parameter: a speed (1 is normal, 0.25 a quarter, 0 a held
/// frame) keyframed over clip time, so a speed ramp is two keyframes and an ease between them.
/// Speeds below zero are read as zero; playing backwards is <c>clip.set-reverse</c>.
/// </para>
/// <para>
/// Hold and linear segments are integrated exactly; eased and bezier segments by Simpson's rule
/// over 64 steps, well under a microsecond of source over a minute. The integral is in flicks, so a
/// linear ramp from 1 to 0.5 over two seconds consumes exactly 1.5 seconds of source.
/// </para>
/// </remarks>
public static class TimeRemap
{
    /// <summary>The parameter's name on a clip.</summary>
    public const string Parameter = "remap";

    private const int Steps = 64;

    /// <summary>The source consumed between the clip's start and a clip time.</summary>
    public static Flicks Offset(AnimatedValue speed, Flicks local)
    {
        ArgumentNullException.ThrowIfNull(speed);
        if (local <= Flicks.Zero)
        {
            return new Flicks((long)Math.Round(Speed(speed, Flicks.Zero) * local.Value));
        }

        if (speed is not KeyframedValue { Keyframes.Length: > 0 } keyed)
        {
            return new Flicks((long)Math.Round(Speed(speed, Flicks.Zero) * local.Value));
        }

        double total = 0;
        Keyframe first = keyed.Keyframes[0];
        Flicks at = Flicks.Zero;

        // Before the first keyframe its value holds.
        if (first.Time > Flicks.Zero)
        {
            Flicks until = Flicks.Min(first.Time, local);
            total += Value(first) * until.Value;
            at = until;
        }

        for (int index = 0; index + 1 < keyed.Keyframes.Length && at < local; index++)
        {
            Keyframe from = keyed.Keyframes[index];
            Keyframe to = keyed.Keyframes[index + 1];
            if (to.Time <= at)
            {
                continue;
            }

            Flicks start = Flicks.Max(from.Time, at);
            Flicks end = Flicks.Min(to.Time, local);
            double length = to.Time.Value - from.Time.Value;
            double a = start.Value - from.Time.Value;
            double b = end.Value - from.Time.Value;
            double v0 = Value(from);
            double v1 = Value(to);

            total += from.Interp switch
            {
                Interp.Hold => v0 * (b - a),
                Interp.Linear => (v0 * (b - a)) + ((v1 - v0) * ((b * b) - (a * a)) / (2 * length)),
                _ => Simpson(speed, start, end),
            };
            at = end;
        }

        // After the last keyframe its value holds.
        if (at < local)
        {
            total += Value(keyed.Keyframes[^1]) * (local.Value - at.Value);
        }

        return new Flicks((long)Math.Round(total));
    }

    /// <summary>The speed at a clip time, never below zero.</summary>
    public static double Speed(AnimatedValue speed, Flicks local) =>
        AnimationEvaluator.Evaluate(speed, local) is ParamValue.Float value ? Math.Max(0, value.Value) : 1.0;

    /// <summary>The fastest the curve runs over a clip's length, for sizing a read-ahead window.</summary>
    public static double Fastest(AnimatedValue speed, Flicks length)
    {
        ArgumentNullException.ThrowIfNull(speed);
        double fastest = Speed(speed, Flicks.Zero);
        for (int step = 1; step <= Steps; step++)
        {
            fastest = Math.Max(fastest, Speed(speed, new Flicks(length.Value * step / Steps)));
        }

        if (speed is KeyframedValue keyed)
        {
            foreach (Keyframe keyframe in keyed.Keyframes)
            {
                fastest = Math.Max(fastest, Value(keyframe));
            }
        }

        return fastest;
    }

    private static double Value(Keyframe keyframe) =>
        keyframe.Value is ParamValue.Float value ? Math.Max(0, value.Value) : 1.0;

    private static double Simpson(AnimatedValue speed, Flicks start, Flicks end)
    {
        double width = end.Value - start.Value;
        if (width <= 0)
        {
            return 0;
        }

        double step = width / Steps;
        double sum = Speed(speed, start) + Speed(speed, end);
        for (int index = 1; index < Steps; index++)
        {
            double weight = index % 2 == 1 ? 4 : 2;
            sum += weight * Speed(speed, new Flicks(start.Value + (long)Math.Round(step * index)));
        }

        return sum * step / 3;
    }
}
