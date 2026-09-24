using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Changes the value of an existing keyframe.</summary>
/// <param name="OwnerId">The clip, track, effect or mask.</param>
/// <param name="Param">The parameter name.</param>
/// <param name="At">The keyframe's time on the sequence.</param>
/// <param name="Value">The new value, as text.</param>
/// <param name="Local">Read the time as relative to the clip's start rather than the sequence's.</param>
[Command("keyframe.set-value", Description = "Change a keyframe's value")]
public sealed record SetKeyframeValueCommand(
    [property: Arg(0, "The clip, track, effect or mask id")] string OwnerId,
    [property: Arg(1, "The parameter name")] string Param,
    [property: Option("at", "The keyframe's time on the sequence")] Flicks At,
    [property: Option("value", "The new value")] string Value,
    [property: Option("local", "Read --at from the clip's start rather than the sequence's")] bool Local = false) : ICommand;
