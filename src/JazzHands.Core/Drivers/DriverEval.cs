using System.Numerics;
using JazzHands.Core.Animation;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Drivers;

/// <summary>Evaluating a driven parameter, and moving values between parameters and drivers.</summary>
public static class DriverEval
{
    /// <summary>
    /// A driven parameter's value at a time from its owner's start, in the type of the value it
    /// drives: the expression over the keyframed or fixed value underneath, in the environment and
    /// origin <see cref="DriverScope"/> has.
    /// </summary>
    public static ParamValue Evaluate(DrivenValue driven, Flicks local, AnimationEvaluator? evaluator = null)
    {
        ArgumentNullException.ThrowIfNull(driven);

        ParamValue under = evaluator is null ? AnimationEvaluator.Evaluate(driven.Base, local) : evaluator.Eval(driven.Base, local);
        DriverExpression expression;
        try
        {
            expression = DriverExpression.Parse(driven.Expression);
        }
        catch (DriverSyntaxException)
        {
            // A hand edit that does not read leaves the value underneath; validation says why.
            return under;
        }

        DriverValue result = expression.Evaluate(new DriverFrame(local, ToDriver(under), DriverScope.Environment, DriverScope.Origin));
        return FromDriver(result, under);
    }

    /// <summary>A parameter value as numbers: a switch 0 or 1, a colour its four channels.</summary>
    public static DriverValue ToDriver(ParamValue value) => value switch
    {
        ParamValue.Float number => DriverValue.Of(number.Value),
        ParamValue.Int whole => DriverValue.Of(whole.Value),
        ParamValue.Bool flag => DriverValue.Of(flag.Value ? 1 : 0),
        ParamValue.Float2 pair => new DriverValue(new Vector4(pair.Value, 0, 0), 2),
        ParamValue.Float4 four => new DriverValue(four.Value, 4),
        ParamValue.Color colour => new DriverValue(colour.Value, 4),
        _ => DriverValue.Of(0),
    };

    /// <summary>Numbers back as a value of the same kind as <paramref name="like"/>; kinds a driver cannot make keep <paramref name="like"/>.</summary>
    public static ParamValue FromDriver(DriverValue value, ParamValue like) => like switch
    {
        ParamValue.Float => new ParamValue.Float(value.X),
        ParamValue.Int => new ParamValue.Int((int)MathF.Round(value.X, MidpointRounding.AwayFromZero)),
        ParamValue.Bool => new ParamValue.Bool(value.X >= 0.5f),
        ParamValue.Float2 => new ParamValue.Float2(new Vector2(value[0], value[1])),
        ParamValue.Float4 => new ParamValue.Float4(new Vector4(value[0], value[1], value[2], value[3])),
        ParamValue.Color => new ParamValue.Color(new Vector4(value[0], value[1], value[2], value[3])),
        _ => like,
    };

    /// <summary>True for a kind of value a driver can make: numbers, pairs, fours, colours, whole numbers and switches.</summary>
    public static bool CanDrive(ParamValue value) =>
        value is ParamValue.Float or ParamValue.Int or ParamValue.Bool or ParamValue.Float2 or ParamValue.Float4 or ParamValue.Color;
}

/// <summary>Smooth one-dimensional gradient noise from -1 to 1: wiggle's, and the shake's.</summary>
public static class DriverNoise
{
    /// <summary>
    /// The noise at a position, a different stream for each seed and channel, whole numbers of
    /// <paramref name="x"/> being where it passes through zero.
    /// </summary>
    public static double Noise(double x, uint seed, uint channel)
    {
        double cell = Math.Floor(x);
        double f = x - cell;
        long whole = (long)cell;
        double g0 = Gradient(whole, seed, channel);
        double g1 = Gradient(whole + 1, seed, channel);
        double fade = f * f * f * ((f * ((f * 6) - 15)) + 10);

        // Two gradients meeting in the middle of the cell peak at a quarter; doubling brings it near one.
        return Math.Clamp(2.0 * ((g0 * f) + (((g1 * (f - 1)) - (g0 * f)) * fade)), -1.0, 1.0);
    }

    private static double Gradient(long cell, uint seed, uint channel)
    {
        uint hash = Pcg((uint)cell ^ Pcg((uint)(cell >> 32) ^ Pcg(seed ^ Pcg(channel))));
        return ((hash & 0xFFFFFF) / (double)0xFFFFFF * 2.0) - 1.0;
    }

    private static uint Pcg(uint value)
    {
        uint state = (value * 747796405u) + 2891336453u;
        uint word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
        return (word >> 22) ^ word;
    }
}
