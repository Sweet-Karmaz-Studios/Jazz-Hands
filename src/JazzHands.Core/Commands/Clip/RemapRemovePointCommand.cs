namespace JazzHands.Core.Commands;

/// <summary>Removes a point from a clip's speed curve (Phase 45); the clip's length follows.</summary>
/// <param name="ClipId">The clip.</param>
/// <param name="Point">Which point, from 1 in time order.</param>
[Command("clip.remap-remove-point", Description = "Remove a point from a clip's speed curve")]
public sealed record RemapRemovePointCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("point", "Which point, from 1 in time order")] int Point) : ICommand;
