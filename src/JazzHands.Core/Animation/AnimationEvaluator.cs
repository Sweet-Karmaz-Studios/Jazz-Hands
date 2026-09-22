using System.Numerics;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Animation;

/// <summary>
/// Works out what an animated parameter is worth at a given time.
/// </summary>
/// <remarks>
/// Called once per animated parameter per frame, for every clip and effect on screen, so it
/// allocates nothing and remembers the segment it used last: scrubbing and playback ask for
/// times near the previous one, and a linear probe from there beats a binary search almost
/// always. Create one per parameter, or share one and accept the occasional miss.
///
/// Outside the keyframed range the nearest value holds. That is what editors do, and it means a
/// parameter with one keyframe is simply constant.
/// </remarks>
public sealed class AnimationEvaluator
{
    private const int MaxNewtonIterations = 8;
    private const float NewtonTolerance = 1e-5f;

    private int _lastSegment;

    /// <summary>Evaluates a parameter at a time, without any caching.</summary>
    public static ParamValue Evaluate(AnimatedValue value, Flicks time) =>
        new AnimationEvaluator().Eval(value, time);

    /// <summary>Evaluates a parameter at a time, remembering the segment for the next call.</summary>
    public ParamValue Eval(AnimatedValue value, Flicks time)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value is StaticValue constant)
        {
            return constant.Value;
        }

        var animated = (KeyframedValue)value;
        EquatableArray<Keyframe> keyframes = animated.Keyframes;

        if (keyframes.IsEmpty)
        {
            throw new ArgumentException("A keyframed parameter with no keyframes has no value.", nameof(value));
        }

        // Strictly before the first keyframe, not at it: two keyframes sharing a time are how a
        // step change is written, and at that instant the later one is the one that applies.
        if (keyframes.Length == 1 || time < keyframes[0].Time)
        {
            return keyframes[0].Value;
        }

        if (time >= keyframes[^1].Time)
        {
            return keyframes[^1].Value;
        }

        int index = FindSegment(keyframes, time);
        Keyframe from = keyframes[index];
        Keyframe to = keyframes[index + 1];

        // Discrete types and Hold never blend: half of "Screen" is not a blend mode.
        if (from.Interp == Interp.Hold || !from.Value.IsContinuous || !to.Value.IsContinuous)
        {
            return from.Value;
        }

        // The segment always spans some time: FindSegment picks the last keyframe at or before
        // the time, so two keyframes sharing a time can never end up as the two ends of a
        // segment. That is what makes the division below safe without a guard.
        Flicks span = to.Time - from.Time;
        float linear = (float)((double)(time - from.Time).Value / span.Value);
        float eased = ApplyEasing(from, to, linear);

        return Interpolate(from.Value, to.Value, eased);
    }

    /// <summary>Blends two values of the same kind. Mismatched kinds hold the first.</summary>
    public static ParamValue Interpolate(ParamValue from, ParamValue to, float t)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        return (from, to) switch
        {
            (ParamValue.Float a, ParamValue.Float b) =>
                new ParamValue.Float(Lerp(a.Value, b.Value, t)),

            (ParamValue.Float2 a, ParamValue.Float2 b) =>
                new ParamValue.Float2(Vector2.Lerp(a.Value, b.Value, t)),

            (ParamValue.Float4 a, ParamValue.Float4 b) =>
                new ParamValue.Float4(Vector4.Lerp(a.Value, b.Value, t)),

            // Colours are linear and premultiplied, so a straight lerp of all four channels is
            // the correct blend; doing it on straight alpha would darken the midpoint.
            (ParamValue.Color a, ParamValue.Color b) =>
                new ParamValue.Color(Vector4.Lerp(a.Value, b.Value, t)),

            _ => from,
        };
    }

    /// <summary>The control points for an interpolation mode, in normalised segment space.</summary>
    public static (Vector2 Out, Vector2 In) HandlesFor(Keyframe from, Keyframe to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        return from.Interp switch
        {
            Interp.EaseIn => (new Vector2(0.42f, 0.0f), new Vector2(1.0f, 1.0f)),
            Interp.EaseOut => (new Vector2(0.0f, 0.0f), new Vector2(0.58f, 1.0f)),
            Interp.EaseInOut => (new Vector2(0.42f, 0.0f), new Vector2(0.58f, 1.0f)),
            Interp.Bezier => (
                from.OutHandle ?? new Vector2(0.42f, 0.0f),
                to.InHandle ?? new Vector2(0.58f, 1.0f)),
            _ => (new Vector2(1.0f / 3.0f, 1.0f / 3.0f), new Vector2(2.0f / 3.0f, 2.0f / 3.0f)),
        };
    }

    /// <summary>
    /// Solves a cubic bezier for y given x, which is what a curve editor's handles describe.
    /// </summary>
    /// <remarks>
    /// The curve is in (time, value) space with the endpoints pinned at (0,0) and (1,1), so x is
    /// the fraction through the segment and y is the fraction of the way between the values.
    /// Newton from a linear guess converges in two or three steps for the handle positions a
    /// person can actually drag; the iteration cap is there for the pathological ones.
    /// </remarks>
    public static float SolveBezier(Vector2 outHandle, Vector2 inHandle, float x)
    {
        if (x <= 0.0f)
        {
            return 0.0f;
        }

        if (x >= 1.0f)
        {
            return 1.0f;
        }

        float t = x;
        for (int iteration = 0; iteration < MaxNewtonIterations; iteration++)
        {
            float currentX = BezierAxis(outHandle.X, inHandle.X, t) - x;
            if (Math.Abs(currentX) < NewtonTolerance)
            {
                break;
            }

            float derivative = BezierDerivative(outHandle.X, inHandle.X, t);
            if (Math.Abs(derivative) < 1e-6f)
            {
                break;
            }

            t -= currentX / derivative;
            t = Math.Clamp(t, 0.0f, 1.0f);
        }

        return BezierAxis(outHandle.Y, inHandle.Y, t);
    }

    private static float ApplyEasing(Keyframe from, Keyframe to, float linear)
    {
        if (from.Interp == Interp.Linear)
        {
            return linear;
        }

        (Vector2 outHandle, Vector2 inHandle) = HandlesFor(from, to);
        return SolveBezier(outHandle, inHandle, linear);
    }

    /// <summary>
    /// The index of the keyframe at or before a time, starting from the segment used last.
    /// </summary>
    private int FindSegment(EquatableArray<Keyframe> keyframes, Flicks time)
    {
        int last = Math.Clamp(_lastSegment, 0, keyframes.Length - 2);

        // The common case: the same segment as last time, or the next one.
        if (keyframes[last].Time <= time && time < keyframes[last + 1].Time)
        {
            return last;
        }

        if (last + 2 < keyframes.Length &&
            keyframes[last + 1].Time <= time &&
            time < keyframes[last + 2].Time)
        {
            _lastSegment = last + 1;
            return last + 1;
        }

        int low = 0;
        int high = keyframes.Length - 1;
        while (low < high - 1)
        {
            int middle = (low + high) / 2;
            if (keyframes[middle].Time <= time)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        _lastSegment = low;
        return low;
    }

    private static float Lerp(float from, float to, float t) => from + ((to - from) * t);

    /// <summary>One axis of a cubic bezier with endpoints pinned at 0 and 1.</summary>
    private static float BezierAxis(float control1, float control2, float t)
    {
        float inverse = 1.0f - t;
        return (3.0f * inverse * inverse * t * control1)
            + (3.0f * inverse * t * t * control2)
            + (t * t * t);
    }

    private static float BezierDerivative(float control1, float control2, float t)
    {
        float inverse = 1.0f - t;
        return (3.0f * inverse * inverse * control1)
            + (6.0f * inverse * t * (control2 - control1))
            + (3.0f * t * t * (1.0f - control2));
    }
}
