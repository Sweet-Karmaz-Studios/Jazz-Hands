namespace JazzHands.Core.Commands;

/// <summary>Wires one node of a comp graph into a port of another (Phase 49).</summary>
/// <remarks>
/// <paramref name="NodeId"/> reads <paramref name="From"/>'s picture through <paramref name="Port"/>
/// (its first port when not given), replacing what was wired there. A merge's ports are
/// <c>background</c>, <c>foreground</c> and <c>mask</c>; a matte's <c>input</c> and <c>matte</c>; a
/// 3D render's <c>1</c> to <c>8</c>; anything else that reads has <c>input</c>. A wire that would go
/// round in a circle is refused with <c>would-cycle</c>; one from a node the graph does not have
/// with <c>node-not-found</c>. One undo step.
/// </remarks>
/// <param name="NodeId">The node that reads.</param>
/// <param name="From">The node it reads.</param>
/// <param name="Port">Which of its ports.</param>
[Command("comp.node-connect", Description = "Wire one comp node into a port of another")]
public sealed record ConnectCompNodeCommand(
    [property: Arg(0, "The node that reads")] string NodeId,
    [property: Arg(1, "The node it reads")] string From,
    [property: Option("port", "Which of its ports: input, background, foreground, mask, matte, or 1 to 8; its first free one when left out")] string? Port = null) : ICommand;
