namespace JazzHands.Core.Commands;

/// <summary>Removes a node from a colour graph (Phase 44).</summary>
/// <remarks>
/// Whatever read the node reads what it read instead, so the rest of the grade stays connected;
/// a node keyed by it is no longer keyed. One undo step.
/// </remarks>
/// <param name="NodeId">The node.</param>
[Command("color.node-remove", Description = "Remove a node from a colour graph")]
public sealed record RemoveColorNodeCommand(
    [property: Arg(0, "The node")] string NodeId) : ICommand;
