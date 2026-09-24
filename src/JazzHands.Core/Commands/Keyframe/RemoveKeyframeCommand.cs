using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>
/// Removes a keyframe. Removing the last one leaves the parameter constant at that keyframe's
/// value, so the picture does not jump.
/// </summary>
/// <param name="OwnerId">The clip, track, effect or mask.</param>
/// <param name="Param">The parameter name.</param>
/// <param name="At">The keyframe's time on the sequence; within half a frame finds it.</param>
/// <param name="Local">Read the time as relative to the clip's start rather than the sequence's.</param>
[Command("keyframe.remove", Description = "Remove a keyframe from a parameter")]
public sealed record RemoveKeyframeCommand(
    [property: Arg(0, "The clip, track, effect or mask id")] string OwnerId,
    [property: Arg(1, "The parameter name")] string Param,
    [property: Option("at", "The keyframe's time on the sequence")] Flicks At,
    [property: Option("local", "Read --at from the clip's start rather than the sequence's")] bool Local = false) : ICommand;
