namespace JazzHands.Core.Commands;

/// <summary>Changes a node of a colour graph (Phase 44).</summary>
/// <remarks>
/// A parameter of the node's correction (the same as <c>param.set</c> on the node's id), a mix's
/// shares, taking its key away, switching it off, or making it the node the graph shows. One
/// undo step for all of them.
/// </remarks>
/// <param name="NodeId">The node.</param>
/// <param name="Param">A parameter of its correction.</param>
/// <param name="Value">The parameter's value.</param>
/// <param name="Weights">A mix's share of each input, in order, such as 1,1.</param>
/// <param name="NoKey">Take its key away, so it applies everywhere (within its masks).</param>
/// <param name="Output">Make it the node the graph shows.</param>
/// <param name="Enabled">Switch it on or off; off, it passes its picture on.</param>
[Command("color.node-set", Description = "Change a node of a colour graph")]
public sealed record SetColorNodeCommand(
    [property: Arg(0, "The node")] string NodeId,
    [property: Option("param", "A parameter of its correction")] string? Param = null,
    [property: Option("value", "The parameter's value")] string? Value = null,
    [property: Option("weights", "A mix's share of each input, in order, such as 1,1")] string? Weights = null,
    [property: Option("no-key", "Take its key away")] bool NoKey = false,
    [property: Option("output", "Make it the node the graph shows")] bool Output = false,
    [property: Option("enabled", "true to switch it on, false to switch it off")] bool? Enabled = null) : ICommand;
