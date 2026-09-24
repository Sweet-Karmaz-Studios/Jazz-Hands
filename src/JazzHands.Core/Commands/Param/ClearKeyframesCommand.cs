using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>
/// Turns animation off: every keyframe goes and the parameter keeps one value, the one it had at
/// <c>--at</c>, or at its first keyframe when no time is given.
/// </summary>
/// <param name="OwnerId">The clip, track, effect or mask.</param>
/// <param name="Param">The parameter name.</param>
/// <param name="At">Keep the value from this time on the sequence.</param>
/// <param name="Local">Read the time as relative to the clip's start rather than the sequence's.</param>
[Command("param.clear-keyframes", Description = "Remove every keyframe from a parameter")]
public sealed record ClearKeyframesCommand(
    [property: Arg(0, "The clip, track, effect or mask id")] string OwnerId,
    [property: Arg(1, "The parameter name")] string Param,
    [property: Option("at", "Keep the value from this time; the first keyframe's when not given")] Flicks? At = null,
    [property: Option("local", "Read --at from the clip's start rather than the sequence's")] bool Local = false) : ICommand;
