using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Puts nodes of a comp graph into a group of their own (Phase 49a).</summary>
/// <remarks>
/// The nodes move into a <c>comp.group</c> node where they were, inside a graph that starts with
/// an In for each node outside they read and ends with an Out, so the picture is unchanged: those
/// nodes are wired into the group's ports (<c>input</c> to <c>input4</c>), each In naming its
/// port, and what read them now reads the group. They must all be in one graph, read at most four
/// nodes outside, and be read from outside through at most one of them; the In and the Out stay
/// where they are. One undo step.
/// </remarks>
/// <param name="NodeIds">The nodes to group.</param>
/// <param name="GroupId">The identifier for the group.</param>
[Command("comp.group", Description = "Put nodes of a comp graph into a group")]
public sealed record GroupCompNodesCommand(
    [property: Arg(0, "Comma-separated node ids")] EquatableArray<string> NodeIds,
    [property: Option("id", "The identifier for the group")] string? GroupId = null) : ICommand;
