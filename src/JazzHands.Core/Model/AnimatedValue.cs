using System.Numerics;
using JazzHands.Core.Time;

namespace JazzHands.Core.Model;

/// <summary>How a keyframe leads into the next one.</summary>
public enum Interp
{
    /// <summary>Hold this value until the next keyframe, then jump. The only mode for discrete types.</summary>
    Hold,

    /// <summary>Straight line to the next value.</summary>
    Linear,

    /// <summary>Cubic bezier using this keyframe's out handle and the next one's in handle.</summary>
    Bezier,

    /// <summary>Bezier preset (0.42, 0, 1, 1): starts slowly, arrives at speed.</summary>
    EaseIn,

    /// <summary>Bezier preset (0, 0, 0.58, 1): starts at speed, arrives slowly.</summary>
    EaseOut,

    /// <summary>Bezier preset (0.42, 0, 0.58, 1): slow at both ends.</summary>
    EaseInOut,
}

/// <summary>
/// One point on a parameter's curve.
/// </summary>
/// <remarks>
/// Times are relative to the clip start, so moving a clip carries its animation with it. For
/// track and sequence level parameters they are relative to the sequence start. See the
/// keyframes skill.
/// </remarks>
/// <param name="Time">When this value applies, relative to the owner.</param>
/// <param name="Value">The value at that time.</param>
/// <param name="Interp">How the curve leaves this keyframe.</param>
/// <param name="InHandle">Bezier control point arriving at this keyframe, in normalised segment space.</param>
/// <param name="OutHandle">Bezier control point leaving this keyframe, in normalised segment space.</param>
public sealed record Keyframe(
    Flicks Time,
    ParamValue Value,
    Interp Interp = Interp.Linear,
    Vector2? InHandle = null,
    Vector2? OutHandle = null) : IEquatable<Keyframe>;

/// <summary>A parameter that may or may not change over time.</summary>
public abstract record AnimatedValue : IEquatable<AnimatedValue>
{
    /// <summary>True when this parameter has keyframes rather than one fixed value.</summary>
    public abstract bool IsAnimated { get; }

    /// <summary>Wraps a constant.</summary>
    public static AnimatedValue Constant(ParamValue value) => new StaticValue(value);

    /// <summary>Wraps a constant float, which is most parameters.</summary>
    public static AnimatedValue Constant(float value) => new StaticValue(new ParamValue.Float(value));
}

/// <summary>A parameter with one value for all time.</summary>
/// <param name="Value">The value.</param>
public sealed record StaticValue(ParamValue Value) : AnimatedValue
{
    /// <inheritdoc />
    public override bool IsAnimated => false;
}

/// <summary>
/// A parameter driven by keyframes, kept sorted by time.
/// </summary>
/// <remarks>
/// The constructor sorts, because every reader assumes sorted order and a hand-edited project
/// file is not obliged to provide it.
/// </remarks>
public sealed record KeyframedValue : AnimatedValue
{
    /// <summary>Creates a keyframed parameter, sorting the keyframes by time.</summary>
    public KeyframedValue(EquatableArray<Keyframe> keyframes) =>
        Keyframes = keyframes.IsEmpty
            ? keyframes
            : new EquatableArray<Keyframe>(keyframes.OrderBy(keyframe => keyframe.Time.Value));

    /// <summary>Creates a keyframed parameter from a sequence.</summary>
    public KeyframedValue(IEnumerable<Keyframe> keyframes)
        : this(new EquatableArray<Keyframe>(keyframes))
    {
    }

    /// <summary>The keyframes, in time order.</summary>
    public EquatableArray<Keyframe> Keyframes { get; init; }

    /// <inheritdoc />
    public override bool IsAnimated => !Keyframes.IsEmpty;

    /// <summary>The time of the first keyframe, or zero when there are none.</summary>
    public Flicks Start => Keyframes.IsEmpty ? Flicks.Zero : Keyframes[0].Time;

    /// <summary>The time of the last keyframe, or zero when there are none.</summary>
    public Flicks End => Keyframes.IsEmpty ? Flicks.Zero : Keyframes[^1].Time;
}
