using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Moves a point of a clip's speed curve: to another speed, another time, or both (Phase 45).</summary>
/// <remarks>
/// A point stays between its neighbours. The clip's length follows the curve, as with
/// <c>clip.remap-add-point</c>. Each step of a drag is one of these, and a drag is one undo step.
/// </remarks>
/// <param name="ClipId">The clip.</param>
/// <param name="Point">Which point, from 1 in time order.</param>
/// <param name="At">Where it goes, on the timeline; where it is when left out.</param>
/// <param name="Speed">Its new speed; its own when left out.</param>
[Command("clip.remap-move-point", Description = "Move a point of a clip's speed curve")]
public sealed record RemapMovePointCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("point", "Which point, from 1 in time order")] int Point,
    [property: Option("at", "Where it goes, on the timeline")] Flicks? At = null,
    [property: Option("speed", "Its new speed")] double? Speed = null) : IMergeableCommand
{
    /// <inheritdoc />
    public bool Continues(ICommand previous) => previous switch
    {
        RemapMovePointCommand move => move.ClipId == ClipId && move.Point == Point,
        RemapAddPointCommand add => add.ClipId == ClipId,
        _ => false,
    };
}
