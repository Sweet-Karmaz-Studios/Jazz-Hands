using System.Numerics;
using JazzHands.Core.Animation;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Audio;

/// <summary>
/// An animated number with its keyframes converted to sample positions, evaluated without
/// allocating.
/// </summary>
/// <remarks>
/// <see cref="AnimationEvaluator"/> returns a <see cref="ParamValue"/>, which is a record, so an
/// interpolated value is a new object. That is fine for a frame and not for the audio thread,
/// which evaluates every volume and pan twice a block. This keeps the same rules (a step change
/// is two keyframes at one time and the later one wins; hold and discrete values never blend;
/// eases are the same bezier handles) over plain arrays, and the conversion happens once, when
/// the graph is built.
/// </remarks>
public sealed class ScalarCurve
{
    private readonly long[] _times;
    private readonly float[] _values;
    private readonly bool[] _holds;
    private readonly bool[] _discrete;
    private readonly bool[] _linear;
    private readonly Vector2[] _outHandles;
    private readonly Vector2[] _inHandles;

    private ScalarCurve(long[] times, float[] values, bool[] holds, bool[] discrete, bool[] linear, Vector2[] outHandles, Vector2[] inHandles)
    {
        _times = times;
        _values = values;
        _holds = holds;
        _discrete = discrete;
        _linear = linear;
        _outHandles = outHandles;
        _inHandles = inHandles;
    }

    /// <summary>True when the value never changes, so a caller can skip evaluating it.</summary>
    public bool IsConstant => _values.Length == 1;

    /// <summary>A value that never changes.</summary>
    public static ScalarCurve Constant(float value) => new([0], [value], [true], [true], [false], [default], [default]);

    /// <summary>
    /// Converts a model parameter.
    /// </summary>
    /// <param name="value">The parameter, or null for the default.</param>
    /// <param name="fallback">The value when the parameter is absent or is not a number.</param>
    /// <param name="sampleRate">The rate keyframe times are converted to.</param>
    public static ScalarCurve From(AnimatedValue? value, float fallback, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        switch (value)
        {
            case null:
                return Constant(fallback);

            case StaticValue constant:
                return Constant(constant.Value is ParamValue.Float number ? number.Value : fallback);

            case KeyframedValue { Keyframes.IsEmpty: false } animated:
            {
                EquatableArray<Keyframe> keys = animated.Keyframes;
                int count = keys.Length;
                long[] times = new long[count];
                float[] values = new float[count];
                bool[] holds = new bool[count];
                bool[] discrete = new bool[count];
                bool[] linear = new bool[count];
                Vector2[] outHandles = new Vector2[count];
                Vector2[] inHandles = new Vector2[count];

                for (int index = 0; index < count; index++)
                {
                    Keyframe key = keys[index];
                    times[index] = key.Time.ToSamples(sampleRate, RoundingMode.Nearest);
                    values[index] = key.Value is ParamValue.Float keyValue ? keyValue.Value : fallback;
                    discrete[index] = key.Value is not ParamValue.Float;
                    holds[index] = key.Interp == Interp.Hold || discrete[index];
                    linear[index] = key.Interp == Interp.Linear;

                    if (index + 1 < count)
                    {
                        (outHandles[index], inHandles[index]) = AnimationEvaluator.HandlesFor(key, keys[index + 1]);
                    }
                }

                return count == 1
                    ? Constant(values[0])
                    : new ScalarCurve(times, values, holds, discrete, linear, outHandles, inHandles);
            }

            default:
                return Constant(fallback);
        }
    }

    /// <summary>The value at a sample position, relative to whatever owns the curve.</summary>
    public float Evaluate(long sample)
    {
        long[] times = _times;
        int last = times.Length - 1;

        if (last == 0 || sample < times[0])
        {
            return _values[0];
        }

        if (sample >= times[last])
        {
            return _values[last];
        }

        // The last keyframe at or before the sample, so two at the same time resolve to the later.
        int low = 0;
        int high = last;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (times[middle] <= sample)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        int from = low;
        int to = from + 1;

        if (_holds[from] || _discrete[to])
        {
            return _values[from];
        }

        float linearT = (float)((double)(sample - times[from]) / (times[to] - times[from]));
        float eased = _linear[from] ? linearT : AnimationEvaluator.SolveBezier(_outHandles[from], _inHandles[from], linearT);

        return _values[from] + ((_values[to] - _values[from]) * eased);
    }
}
