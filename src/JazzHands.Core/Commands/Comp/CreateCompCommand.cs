namespace JazzHands.Core.Commands;

/// <summary>Gives a clip a comp graph (Phase 49).</summary>
/// <remarks>
/// A <c>comp.graph</c> effect goes first in the clip's chain with two nodes, the clip's picture
/// coming in (<c>comp.in</c>) wired to the output (<c>comp.out</c>), so the clip looks as it did.
/// Nodes added with <c>comp.node-add</c> and wired with <c>comp.node-connect</c> then make its
/// picture; the clip's own effects after the graph, its opacity and blend still apply. One undo step.
/// </remarks>
/// <param name="ClipId">The clip.</param>
/// <param name="GraphId">The identifier for the graph.</param>
[Command("comp.create", Description = "Give a clip a compositing node graph")]
public sealed record CreateCompCommand(
    [property: Arg(0, "The clip")] string ClipId,
    [property: Option("id", "The identifier for the graph")] string? GraphId = null) : ICommand;
