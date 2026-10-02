namespace JazzHands.Core.Commands;

/// <summary>Takes a node out of its comp graph, and every wire from it (Phase 49). One undo step.</summary>
/// <param name="NodeId">The node.</param>
[Command("comp.node-remove", Description = "Take a node out of a comp graph")]
public sealed record RemoveCompNodeCommand(
    [property: Arg(0, "The node")] string NodeId) : ICommand;
