using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Sets one of an effect's parameters.</summary>
/// <remarks>
/// The same as <c>param.set</c>, for an effect: a constant, or with <c>--at</c> on a parameter
/// that already has keyframes, a keyframe at that time.
/// </remarks>
/// <param name="EffectId">Which effect.</param>
/// <param name="Param">The parameter name, as <c>jazz effect list</c> shows it.</param>
/// <param name="Value">The value, as text: 12, "100, 50", "#FF8800", both.</param>
/// <param name="At">For a keyframed parameter, the keyframe's time on the sequence.</param>
/// <param name="Local">Read the time as relative to the clip's start rather than the sequence's.</param>
[Command("effect.set-param", Description = "Set a parameter of an effect")]
public sealed record SetEffectParamCommand(
    [property: Arg(0, "The effect id")] string EffectId,
    [property: Arg(1, "The parameter name")] string Param,
    [property: Arg(2, "The value, for example 12, '100, 50' or #FF8800")] string Value,
    [property: Option("at", "For a keyframed parameter, the keyframe's time on the sequence")] Flicks? At = null,
    [property: Option("local", "Read --at from the clip's start rather than the sequence's")] bool Local = false) : ICommand;
