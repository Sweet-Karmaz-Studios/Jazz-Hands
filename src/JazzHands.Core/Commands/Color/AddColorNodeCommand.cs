namespace JazzHands.Core.Commands;

/// <summary>Adds a node to a colour graph (Phase 44).</summary>
/// <remarks>
/// Serial by default: the new node follows <paramref name="After"/> (the graph's output when not
/// given), takes its picture and hands its own on to whatever read it. With
/// <paramref name="ParallelTo"/> it reads the same picture as that node instead, and the two are
/// mixed: into the mix that node already feeds, or a new one in its place. Given a clip, the
/// node goes into the clip's first colour graph, which is added at the end of its chain when it
/// has none. One undo step.
/// </remarks>
/// <param name="Target">A colour graph effect, or a clip.</param>
/// <param name="Type">What the node does: wheels, curves, hsl, lut or white-balance.</param>
/// <param name="After">The node to follow.</param>
/// <param name="ParallelTo">The node to sit beside, mixed with it.</param>
/// <param name="NodeId">The identifier for the new node.</param>
[Command("color.node-add", Description = "Add a node to a colour graph")]
public sealed record AddColorNodeCommand(
    [property: Arg(0, "A colour graph effect, or a clip whose first colour graph takes it (one is added when it has none)")] string Target,
    [property: Option("type", "What the node does: wheels, curves, hsl, lut or white-balance")] string Type = "wheels",
    [property: Option("after", "The node to follow; the graph's output when not given")] string? After = null,
    [property: Option("parallel-to", "A node to sit beside, reading the same picture, the two mixed")] string? ParallelTo = null,
    [property: Option("id", "The identifier for the new node")] string? NodeId = null) : ICommand;
