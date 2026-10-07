using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Moves a keyframe to another time, keeping its shape, and its value unless given one.</summary>
/// <remarks>
/// A drag that moves one keyframe step by step (the timeline's rubber band) sends one of these
/// per step, each starting where the last one left the keyframe; they are one undo step.
/// </remarks>
/// <param name="OwnerId">The clip, track, effect or mask.</param>
/// <param name="Param">The parameter name.</param>
/// <param name="At">The keyframe's time on the sequence.</param>
/// <param name="To">Its new time on the sequence.</param>
/// <param name="Local">Read both times as relative to the clip's start rather than the sequence's.</param>
/// <param name="Value">Also give it this value, as text, as <c>keyframe.set-value</c> would; null keeps its value.</param>
[Command("keyframe.move", Description = "Move a keyframe to another time")]
public sealed record MoveKeyframeCommand(
    [property: Arg(0, "The clip, track, effect or mask id")] string OwnerId,
    [property: Arg(1, "The parameter name")] string Param,
    [property: Option("at", "The keyframe's time on the sequence")] Flicks At,
    [property: Option("to", "Its new time on the sequence")] Flicks To,
    [property: Option("local", "Read the times from the clip's start rather than the sequence's")] bool Local = false,
    [property: Option("value", "Also give it this value; it keeps its own without")] string? Value = null) : IMergeableCommand
{
    /// <inheritdoc />
    public bool Continues(ICommand previous) =>
        previous is MoveKeyframeCommand before
        && before.OwnerId == OwnerId
        && before.Param == Param
        && before.Local == Local
        && before.To == At;
}
