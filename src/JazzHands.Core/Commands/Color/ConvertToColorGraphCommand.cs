namespace JazzHands.Core.Commands;

/// <summary>Turns a colour effect into a colour graph of one node (Phase 44).</summary>
/// <remarks>
/// The effect keeps its id, parameters and masks as the graph's first node, and the graph takes
/// its place in the chain, so the picture does not change; more nodes can then go beside it or
/// after it. One undo step.
/// </remarks>
/// <param name="EffectId">A Colour Wheels, Curves, HSL qualifier, LUT or White balance effect in a chain.</param>
/// <param name="GraphId">The identifier for the graph.</param>
[Command("color.to-graph", Description = "Turn a colour effect into a node graph")]
public sealed record ConvertToColorGraphCommand(
    [property: Arg(0, "A Colour Wheels, Curves, HSL qualifier, LUT or White balance effect")] string EffectId,
    [property: Option("id", "The identifier for the graph")] string? GraphId = null) : ICommand;
