namespace JazzHands.Core.Commands;

/// <summary>Shows one comp graph node's picture in the program monitor, or the program again (Phase 49).</summary>
/// <remarks>
/// The graph that has the node shows that node's picture in place of its output, so any step of a
/// comp can be looked at while it plays. Needs a running editor; a still of a node is
/// <c>jazz frame --node</c> or <c>render_frame</c> with <c>node</c>.
/// </remarks>
/// <param name="NodeId">The node; the program again when left out.</param>
[Command("comp.view",
    Description = "Show a comp node's picture in the program monitor",
    Undoable = false,
    NotUndoableReason = "It is how the monitor shows the graph. It does not change the project.")]
public sealed record ViewCompNodeCommand(
    [property: Arg(0, "The node; the program when left out")] string? NodeId = null) : ICommand;
