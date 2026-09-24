using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>
/// Sets a keyframe's bezier handles and makes its curve a bezier.
/// </summary>
/// <remarks>
/// Handles are in the segment's own space, time then value, each from 0 to 1: the out handle
/// shapes the curve leaving this keyframe, the in handle the curve arriving at it. "0.42, 0" out
/// and "0.58, 1" in on the next keyframe is an ease in and out. Time is kept inside 0 to 1 so the
/// curve can never run backwards.
/// </remarks>
/// <param name="OwnerId">The clip, track, effect or mask.</param>
/// <param name="Param">The parameter name.</param>
/// <param name="At">The keyframe's time on the sequence.</param>
/// <param name="In">The handle arriving at this keyframe, as "time, value".</param>
/// <param name="Out">The handle leaving it, as "time, value".</param>
/// <param name="Local">Read the time as relative to the clip's start rather than the sequence's.</param>
[Command("keyframe.set-handles", Description = "Set a keyframe's bezier handles")]
public sealed record SetKeyframeHandlesCommand(
    [property: Arg(0, "The clip, track, effect or mask id")] string OwnerId,
    [property: Arg(1, "The parameter name")] string Param,
    [property: Option("at", "The keyframe's time on the sequence")] Flicks At,
    [property: Option("in", "The handle arriving, as 'time, value' from 0 to 1")] string? In = null,
    [property: Option("out", "The handle leaving, as 'time, value' from 0 to 1")] string? Out = null,
    [property: Option("local", "Read --at from the clip's start rather than the sequence's")] bool Local = false) : ICommand;
