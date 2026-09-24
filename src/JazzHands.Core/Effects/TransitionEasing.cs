using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Core.Animation;
using JazzHands.Core.Model;

namespace JazzHands.Core.Effects;

/// <summary>
/// How a transition's progress moves from 0 to 1: the two parameters every picture transition
/// takes after its own, and the curve they describe.
/// </summary>
/// <remarks>
/// The registry appends <see cref="Params"/> to every <see cref="EffectKind.Transition"/> type, so
/// a transition written with its attributes never declares them and every one of them eases the
/// same way. The custom curve is a cubic bezier with its ends at (0, 0) and (1, 1), written as
/// CSS writes <c>cubic-bezier(x1, y1, x2, y2)</c>, and solved with the keyframe solver.
/// </remarks>
public static class TransitionEasing
{
    /// <summary>The name of the easing choice.</summary>
    public const string EasingParam = "easing";

    /// <summary>The name of the custom curve.</summary>
    public const string CurveParam = "curve";

    /// <summary>The easing choices, in the order they are shown.</summary>
    public static ImmutableArray<string> Choices { get; } = ["linear", "ease-in", "ease-out", "ease-in-out", "custom"];

    /// <summary>The parameters every picture transition takes after its own.</summary>
    public static ImmutableArray<ParamDescriptor> Params { get; } =
    [
        new ParamDescriptor(
            EasingParam,
            ParamType.Enum,
            new ParamValue.Enum("linear"),
            "Easing",
            "How progress moves through the transition: steadily, slow at one or both ends, or along the custom curve.",
            Animatable: false,
            Choices: new EquatableArray<string>(Choices)),
        new ParamDescriptor(
            CurveParam,
            ParamType.Float4,
            new ParamValue.Float4(new Vector4(0.42f, 0.0f, 0.58f, 1.0f)),
            "Curve",
            "The custom easing as a cubic bezier's two handles, x1, y1, x2, y2, as CSS writes cubic-bezier. Used when easing is custom.",
            Animatable: false),
    ];

    /// <summary>Eases a linear progress with a transition's parameters.</summary>
    public static float Apply(ParameterSet parameters, float linear)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (parameters.Descriptor.IndexOf(EasingParam) < 0)
        {
            return Math.Clamp(linear, 0.0f, 1.0f);
        }

        return Apply(parameters.Enum(EasingParam), parameters.Float4(CurveParam), linear);
    }

    /// <summary>Eases a linear progress by name, with the custom curve's handles.</summary>
    public static float Apply(string easing, Vector4 curve, float linear)
    {
        float x = Math.Clamp(linear, 0.0f, 1.0f);

        return easing switch
        {
            "ease-in" => AnimationEvaluator.SolveBezier(new Vector2(0.42f, 0.0f), new Vector2(1.0f, 1.0f), x),
            "ease-out" => AnimationEvaluator.SolveBezier(new Vector2(0.0f, 0.0f), new Vector2(0.58f, 1.0f), x),
            "ease-in-out" => AnimationEvaluator.SolveBezier(new Vector2(0.42f, 0.0f), new Vector2(0.58f, 1.0f), x),

            // The handles' x is kept inside the segment, or the curve would double back in time.
            "custom" => AnimationEvaluator.SolveBezier(
                new Vector2(Math.Clamp(curve.X, 0.0f, 1.0f), curve.Y),
                new Vector2(Math.Clamp(curve.Z, 0.0f, 1.0f), curve.W),
                x),
            _ => x,
        };
    }
}
