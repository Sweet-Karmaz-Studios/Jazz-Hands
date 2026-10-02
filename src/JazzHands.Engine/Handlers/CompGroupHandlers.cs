using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>How deep groups go, which a group or a node added must keep under <see cref="CompGraph.MostDepth"/> (Phase 49a).</summary>
internal static class CompDepth
{
    /// <summary>How many groups hold a graph: 0 for a clip's own graph.</summary>
    internal static int Of(Project project, ParamOwner holder)
    {
        int depth = 0;
        ParamOwner current = holder;
        while (current.Effect?.TypeId == CompGraph.Group && current.Graph is { } parent && ParamTargets.Find(project, parent.Id) is { } up)
        {
            depth++;
            current = up;
        }

        return depth;
    }

    /// <summary>How many groups deep a node goes inside itself: 1 for a group of plain nodes, 0 for anything else.</summary>
    internal static int Inside(CompNode node) =>
        node.Effect is { TypeId: CompGraph.Group, Comp: { } inner } ? 1 + inner.Nodes.Select(Inside).DefaultIfEmpty(0).Max() : 0;

    /// <summary>Refuses a change that would put a group deeper than the most there may be.</summary>
    internal static void Require(int depth)
    {
        if (depth >= CompGraph.MostDepth)
        {
            throw new CommandException("too-deep", $"That would put a group {depth + 1} deep; groups go at most {CompGraph.MostDepth} deep.", "nodeIds");
        }
    }
}

/// <summary>Puts nodes into a group of their own.</summary>
public sealed class GroupCompNodesHandler : ICommandHandler<GroupCompNodesCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, GroupCompNodesCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        string[] ids = command.NodeIds.IsEmpty ? [] : [.. command.NodeIds.Select(id => id.Trim()).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal)];
        if (ids.Length == 0)
        {
            throw new CommandException("no-nodes", "Name the nodes to group.", "nodeIds");
        }

        (CompHelp.Found found, _) = CompHelp.Node(project, ids[0]);
        foreach (string id in ids)
        {
            if (CompHelp.Node(project, id).Found.Holder.Id != found.Holder.Id)
            {
                throw new CommandException("not-one-graph", $"'{id}' is in another graph than '{ids[0]}'. A group is made of nodes of one graph.", "nodeIds");
            }
        }

        CompGraph graph = found.Graph;
        var inside = new HashSet<string>(ids, StringComparer.Ordinal);
        CompNode[] chosen = [.. graph.Nodes.Where(node => inside.Contains(node.Id))];
        if (chosen.Any(node => node.Effect.TypeId is CompGraph.In or CompGraph.Out))
        {
            throw new CommandException("in-or-out", "The In and the Out stay where they are; group the nodes between them.", "nodeIds");
        }

        // Wires across the edge: those coming in become the group's input, those going out its output.
        CompInput[] coming = [.. chosen.SelectMany(node => node.Inputs).Where(input => !inside.Contains(input.From))];
        CompInput[] going = [.. graph.Nodes.Where(node => !inside.Contains(node.Id)).SelectMany(node => node.Inputs).Where(input => inside.Contains(input.From))];
        if (coming.Concat(going).FirstOrDefault(input => graph.Node(input.From) is { } from && CompGraph.IsObject3D(from.Effect.TypeId)) is { } split)
        {
            throw new CommandException("group-splits-3d", $"'{split.From}' is a 3D object read across the group's edge. A 3D render and the objects wired into it go into a group together.", "nodeIds");
        }

        string[] sources = [.. coming.Select(input => input.From).Distinct(StringComparer.Ordinal)];
        if (sources.Length > 1)
        {
            throw new CommandException("group-inputs", $"A group takes one picture in, and these nodes read {sources.Length} from outside it: {string.Join(", ", sources)}. Group fewer nodes, or what they read too.", "nodeIds");
        }

        string[] given = [.. going.Select(input => input.From).Distinct(StringComparer.Ordinal)];
        if (given.Length > 1)
        {
            throw new CommandException("group-outputs", $"A group gives one picture out, and the rest of the graph reads {given.Length} of these nodes: {string.Join(", ", given)}.", "nodeIds");
        }

        CompDepth.Require(CompDepth.Of(project, found.Holder) + 1 + chosen.Select(CompDepth.Inside).Max());
        string groupId = HandlerHelp.IdOr(command.GroupId);
        HandlerHelp.RequireUnused(project, groupId);

        // What the group gives: the node read from outside, or else the last that none of the others reads.
        string? last = given.FirstOrDefault() ?? chosen.LastOrDefault(node => !chosen.Any(other => other.Inputs.Any(input => input.From == node.Id)))?.Id;
        double left = chosen.Min(node => node.X);
        double right = chosen.Max(node => node.X);
        double middle = Math.Round(chosen.Average(node => node.Y), 1);
        Effect input = Effect.Create(CompGraph.In);
        var inner = new CompGraph(
        [
            new CompNode(input, X: left - CompHelp.Spacing, Y: middle),
            .. chosen.Select(node => node with { Inputs = [.. node.Inputs.Select(wire => inside.Contains(wire.From) ? wire : wire with { From = input.Id })] }),
            new CompNode(Effect.Create(CompGraph.Out), last is null ? [] : [new CompInput("input", last)], X: right + CompHelp.Spacing, Y: middle),
        ]);
        var group = new CompNode(
            Effect.Create(CompGraph.Group) with { Id = groupId, Comp = inner },
            sources.Length == 1 ? [new CompInput("input", sources[0])] : [],
            Math.Round(chosen.Average(node => node.X), 1),
            middle);

        // The group goes where the first of its nodes was; what read them reads it.
        int at = graph.Nodes.IndexOf(node => inside.Contains(node.Id));
        var nodes = new List<CompNode>();
        for (int index = 0; index < graph.Nodes.Length; index++)
        {
            CompNode node = graph.Nodes[index];
            if (index == at)
            {
                nodes.Add(group);
            }

            if (!inside.Contains(node.Id))
            {
                nodes.Add(node with { Inputs = [.. node.Inputs.Select(wire => inside.Contains(wire.From) ? wire with { From = groupId } : wire)] });
            }
        }

        context.Changed(groupId);
        return CompHelp.Replace(project, found, new CompGraph([.. nodes]), context);
    }
}

/// <summary>Takes a group's nodes back out.</summary>
public sealed class UngroupCompNodeHandler : ICommandHandler<UngroupCompNodeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, UngroupCompNodeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (CompHelp.Found found, CompNode group) = CompHelp.Node(project, command.NodeId);
        if (group.Effect.TypeId != CompGraph.Group)
        {
            throw new CommandException("not-a-group", $"'{group.Id}' is a {CompHelp.Name(group)} node, not a group.", "nodeId");
        }

        CompGraph inner = group.Effect.Comp ?? CompGraph.Empty;
        string? source = group.Input("input");
        var ins = new HashSet<string>(inner.Nodes.Where(node => node.Effect.TypeId == CompGraph.In).Select(node => node.Id), StringComparer.Ordinal);
        string? gives = inner.OutputNode?.Input("input");

        // With a picture wired in, the group's Ins become that picture; with none, they stay as the picture coming in.
        bool keepIns = source is null;
        string? Mapped(string from) => !keepIns && ins.Contains(from) ? source : from;
        CompNode[] moved = [.. inner.Nodes
            .Where(node => node.Effect.TypeId != CompGraph.Out && (keepIns || !ins.Contains(node.Id)))
            .Select(node => node with { Inputs = [.. node.Inputs.Select(wire => wire with { From = Mapped(wire.From)! })] })];

        // Placed where the group was, as they were inside it.
        double dx = moved.Length == 0 ? 0 : group.X - moved.Min(node => node.X);
        double dy = moved.Length == 0 ? 0 : group.Y - moved.Min(node => node.Y);
        moved = [.. moved.Select(node => node with { X = Math.Round(node.X + dx, 1), Y = Math.Round(node.Y + dy, 1) })];

        string? replacement = gives is null ? null : Mapped(gives);
        var nodes = new List<CompNode>();
        foreach (CompNode node in found.Graph.Nodes)
        {
            if (node.Id == group.Id)
            {
                nodes.AddRange(moved);
                continue;
            }

            nodes.Add(node with
            {
                Inputs = [.. node.Inputs
                    .Where(wire => wire.From != group.Id || replacement is not null)
                    .Select(wire => wire.From == group.Id ? wire with { From = replacement! } : wire)],
            });
        }

        context.Changed(group.Id);
        return CompHelp.Replace(project, found, new CompGraph([.. nodes]), context);
    }
}
