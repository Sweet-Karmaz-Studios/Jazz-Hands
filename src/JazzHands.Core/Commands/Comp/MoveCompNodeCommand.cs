namespace JazzHands.Core.Commands;

/// <summary>Moves a node in the node view (Phase 49). One undo step; a drag sends one when it ends.</summary>
/// <param name="NodeId">The node.</param>
/// <param name="X">Its left edge.</param>
/// <param name="Y">Its top edge.</param>
[Command("comp.node-move", Description = "Move a node in the comp node view")]
public sealed record MoveCompNodeCommand(
    [property: Arg(0, "The node")] string NodeId,
    [property: Arg(1, "Its left edge")] double X,
    [property: Arg(2, "Its top edge")] double Y) : ICommand;
