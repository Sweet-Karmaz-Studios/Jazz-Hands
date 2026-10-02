namespace JazzHands.Core.Commands;

/// <summary>Takes the wire out of a port of a comp node (Phase 49). One undo step.</summary>
/// <param name="NodeId">The node.</param>
/// <param name="Port">Which port; every one when left out.</param>
[Command("comp.node-disconnect", Description = "Take the wire out of a comp node's port")]
public sealed record DisconnectCompNodeCommand(
    [property: Arg(0, "The node")] string NodeId,
    [property: Option("port", "Which port; every one when left out")] string? Port = null) : ICommand;
