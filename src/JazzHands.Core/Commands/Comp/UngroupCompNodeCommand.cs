namespace JazzHands.Core.Commands;

/// <summary>Takes a comp group's nodes back out into the graph holding it (Phase 49a).</summary>
/// <remarks>
/// The group's nodes keep their ids and places. What its In fed now reads whatever was wired into
/// the group (or, with nothing wired in, the In stays as the picture coming in), and what read the
/// group now reads what reached its Out. One undo step.
/// </remarks>
/// <param name="NodeId">The group.</param>
[Command("comp.ungroup", Description = "Take a comp group's nodes back out")]
public sealed record UngroupCompNodeCommand(
    [property: Arg(0, "The group node")] string NodeId) : ICommand;
