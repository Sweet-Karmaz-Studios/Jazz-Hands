using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Adds a point to a clip's speed curve (Phase 45), turning time remapping on when it was off.</summary>
/// <remarks>
/// At the speed the curve has there unless <c>--speed</c> says otherwise. The clip's length follows
/// the curve so it plays the same stretch of its source, rippling what follows in a magnetic
/// sequence; the clips linked to it (its sound) take the same curve. Points are numbered from 1
/// in time order.
/// </remarks>
/// <param name="ClipId">The clip.</param>
/// <param name="At">Where, on the timeline; put on the sequence's frame.</param>
/// <param name="Speed">The speed there: 1 is normal, 4 is 400%, 0.25 a quarter.</param>
[Command("clip.remap-add-point", Description = "Add a point to a clip's speed curve")]
public sealed record RemapAddPointCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("at", "Where, on the timeline")] Flicks At,
    [property: Option("speed", "The speed there; the curve's own when left out")] double? Speed = null) : ICommand;
