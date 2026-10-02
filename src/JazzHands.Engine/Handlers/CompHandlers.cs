using System.Globalization;
using System.Text;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Effects;

namespace JazzHands.Engine.Handlers;

/// <summary>What the comp graph handlers share (Phase 49).</summary>
internal static class CompHelp
{
    /// <summary>How far apart nodes are put when nobody says where.</summary>
    internal const double Spacing = 150;

    /// <summary>A graph found: the effect holding it, where that sits, and the graph.</summary>
    internal sealed record Found(ParamOwner Holder, CompGraph Graph);

    /// <summary>The graph a graph id or a clip id names: the clip's first comp graph.</summary>
    internal static Found Graph(Project project, string target)
    {
        if (ParamTargets.Find(project, target) is { } owner)
        {
            if (owner.Kind == ParamOwnerKind.Effect && owner.Graph is null && owner.Effect is { TypeId: CompGraph.TypeId } effect)
            {
                return new Found(owner, effect.Comp ?? CompGraph.Empty);
            }

            if (owner.Kind == ParamOwnerKind.Clip
                && owner.Clip!.Effects.FirstOrDefault(candidate => candidate.TypeId == CompGraph.TypeId) is { } held
                && ParamTargets.Find(project, held.Id) is { } holder)
            {
                return new Found(holder, held.Comp ?? CompGraph.Empty);
            }
        }

        throw new CommandException(
            "comp-not-found",
            $"'{target}' is neither a comp graph nor a clip with one. Give the clip one with 'jazz comp create <clip>'.",
            "target");
    }

    /// <summary>The graph a node is in, and the node.</summary>
    internal static (Found Found, CompNode Node) Node(Project project, string nodeId)
    {
        if (ParamTargets.Find(project, nodeId) is { Kind: ParamOwnerKind.Effect, Graph: { Comp: { } comp } holder }
            && comp.Node(nodeId) is { } node
            && ParamTargets.Find(project, holder.Id) is { } holderOwner)
        {
            return (new Found(holderOwner, comp), node);
        }

        throw new CommandException("node-not-found", $"No comp graph has a node '{nodeId}'.", "nodeId");
    }

    /// <summary>The project with a graph changed, refused when the clip's track is locked.</summary>
    internal static Project Replace(Project project, Found found, CompGraph graph, HandlerContext context)
    {
        HandlerHelp.RequireUnlocked(found.Holder.Track);
        context.Changed(found.Holder.Id);
        if (found.Holder.Clip is { } clip)
        {
            context.Changed(clip.Id);
        }

        return ParamTargets.ReplaceEffect(project, found.Holder, found.Holder.Effect! with { Comp = graph });
    }

    /// <summary>The node type a person typed: a short name, a graph type, or a registered video effect or generator.</summary>
    internal static (string TypeId, bool IsGenerator) Type(string typed)
    {
        string type = typed.Trim();
        if (CompNodes.ShortNames.TryGetValue(type, out string? known))
        {
            return (known, false);
        }

        if (CompNodes.Find(type) is not null)
        {
            return (type, false);
        }

        if (EffectCatalog.Registry.Find(type) is { Kind: EffectKind.Video or EffectKind.Generator } descriptor && type != CompGraph.TypeId)
        {
            return (type, descriptor.Kind == EffectKind.Generator);
        }

        throw new CommandException(
            "unknown-type",
            $"'{typed}' is not a node type. There are in, out, media, merge, transform, matte, plane3d, render3d, text3d, shape3d, model3d, camera3d and light3d, and every video effect and generator ('jazz effect list').",
            "type");
    }

    /// <summary>The ports a node in the graph has.</summary>
    internal static IReadOnlyList<string> Ports(CompNode node) =>
        CompGraph.PortsOf(node.Effect.TypeId, EffectCatalog.Registry.Find(node.Effect.TypeId) is { Kind: EffectKind.Generator });

    /// <summary>A readable name for a node: its type's.</summary>
    internal static string Name(CompNode node) =>
        CompNodes.Find(node.Effect.TypeId)?.Name ?? EffectCatalog.Registry.Find(node.Effect.TypeId)?.Name ?? node.Effect.TypeId;
}

/// <summary>Gives a clip a comp graph.</summary>
public sealed class CreateCompHandler : ICommandHandler<CreateCompCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, CreateCompCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);
        Clip clip = found.Clip;
        if (found.Track.Kind != TrackKind.Video || SceneObjects.Is(clip))
        {
            throw new CommandException("not-a-picture", $"'{clip.Name}' has no picture to composite. A comp graph goes on a picture on a video track.", "clipId");
        }

        // An empty graph added from the Effects panel is filled; one with nodes is refused.
        Effect? existing = clip.Effects.FirstOrDefault(effect => effect.TypeId == CompGraph.TypeId);
        if (existing is { Comp.Nodes.IsEmpty: false })
        {
            throw new CommandException("comp-exists", $"'{clip.Name}' already has a comp graph, '{existing.Id}'.", "clipId");
        }

        string id = existing?.Id ?? HandlerHelp.IdOr(command.GraphId);
        if (existing is null)
        {
            HandlerHelp.RequireUnused(project, id);
        }

        Effect input = Effect.Create(CompGraph.In);
        Effect output = Effect.Create(CompGraph.Out);
        var graph = new CompGraph(
        [
            new CompNode(input, X: 0, Y: 0),
            new CompNode(output, [new CompInput("input", input.Id)], X: CompHelp.Spacing * 2, Y: 0),
        ]);

        Effect holder = (existing ?? Effect.Create(CompGraph.TypeId) with { Id = id }) with { Comp = graph };
        EquatableArray<Effect> effects = existing is null
            ? clip.Effects.Insert(0, holder)
            : clip.Effects.SetItem(clip.Effects.IndexOf(effect => effect.Id == existing.Id), holder);

        context.Changed(clip.Id);
        context.Changed(id);
        return project.ReplaceTrack(found.Track.ReplaceClip(clip with { Effects = effects }));
    }
}

/// <summary>Adds a node to a comp graph.</summary>
public sealed class AddCompNodeHandler : ICommandHandler<AddCompNodeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddCompNodeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        CompHelp.Found found = CompHelp.Graph(project, command.Target);
        CompGraph graph = found.Graph;
        (string type, bool generator) = CompHelp.Type(command.Type);
        if (type == CompGraph.Out && graph.OutputNode is { } output)
        {
            throw new CommandException("output-exists", $"The graph already has its output, '{output.Id}'. Wire into it, or remove it first.", "type");
        }

        string id = HandlerHelp.IdOr(command.NodeId);
        HandlerHelp.RequireUnused(project, id);

        CompNode? from = null;
        if (command.From is { Length: > 0 } fromId)
        {
            from = graph.Node(fromId) ?? throw new CommandException("node-not-found", $"The graph has no node '{fromId}'.", "from");
        }

        IReadOnlyList<string> ports = CompGraph.PortsOf(type, generator);
        if (from is not null && ports.Count == 0)
        {
            throw new CommandException("unknown-port", $"A {type} node reads nothing, so it cannot be wired from '{from.Id}'.", "from");
        }

        double x = command.X ?? (from is not null ? from.X + CompHelp.Spacing : graph.Nodes.Select(node => node.X).DefaultIfEmpty(-CompHelp.Spacing).Max() + CompHelp.Spacing);
        double y = command.Y ?? from?.Y ?? 0;
        var node = new CompNode(
            Effect.Create(type) with { Id = id },
            from is null ? [] : [new CompInput(ports[0], from.Id)],
            x,
            y);

        context.Changed(id);
        return CompHelp.Replace(project, found, graph with { Nodes = graph.Nodes.Add(node) }, context);
    }
}

/// <summary>Takes a node out of its graph.</summary>
public sealed class RemoveCompNodeHandler : ICommandHandler<RemoveCompNodeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveCompNodeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (CompHelp.Found found, CompNode node) = CompHelp.Node(project, command.NodeId);
        context.Changed(node.Id);
        return CompHelp.Replace(project, found, found.Graph.Without(node.Id), context);
    }
}

/// <summary>Wires one node into a port of another.</summary>
public sealed class ConnectCompNodeHandler : ICommandHandler<ConnectCompNodeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ConnectCompNodeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (CompHelp.Found found, CompNode node) = CompHelp.Node(project, command.NodeId);
        CompGraph graph = found.Graph;
        if (graph.Node(command.From) is null)
        {
            throw new CommandException("node-not-found", $"The graph has no node '{command.From}' to wire from.", "from");
        }

        if (graph.Reads(command.From, node.Id))
        {
            throw new CommandException("would-cycle", $"'{command.From}' already reads '{node.Id}', so wiring it in would go round in a circle.", "from");
        }

        IReadOnlyList<string> ports = CompHelp.Ports(node);
        string port = command.Port?.Trim() is { Length: > 0 } named
            ? named
            : ports.FirstOrDefault(candidate => node.Input(candidate) is null) ?? ports.FirstOrDefault() ?? string.Empty;
        if (!ports.Contains(port))
        {
            throw new CommandException(
                "unknown-port",
                ports.Count == 0
                    ? $"'{node.Id}' reads nothing, so nothing can be wired into it."
                    : $"'{node.Id}' has no port '{port}'. It has {string.Join(", ", ports)}.",
                "port");
        }

        CompNode wired = node with { Inputs = [.. node.Inputs.Where(input => input.Port != port), new CompInput(port, command.From)] };
        if (wired == node)
        {
            return project;
        }

        context.Changed(node.Id);
        return CompHelp.Replace(project, found, graph.Replace(wired), context);
    }
}

/// <summary>Takes the wire out of a port.</summary>
public sealed class DisconnectCompNodeHandler : ICommandHandler<DisconnectCompNodeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, DisconnectCompNodeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (CompHelp.Found found, CompNode node) = CompHelp.Node(project, command.NodeId);
        CompNode cut = node with { Inputs = [.. node.Inputs.Where(input => command.Port is { Length: > 0 } port && input.Port != port)] };
        if (cut == node)
        {
            return project;
        }

        context.Changed(node.Id);
        return CompHelp.Replace(project, found, found.Graph.Replace(cut), context);
    }
}

/// <summary>Moves a node in the node view.</summary>
public sealed class MoveCompNodeHandler : ICommandHandler<MoveCompNodeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, MoveCompNodeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (!double.IsFinite(command.X) || !double.IsFinite(command.Y))
        {
            throw new CommandException("invalid-value", "A node's place is two finite numbers.", "x");
        }

        (CompHelp.Found found, CompNode node) = CompHelp.Node(project, command.NodeId);
        CompNode moved = node with { X = Math.Round(command.X, 1), Y = Math.Round(command.Y, 1) };
        if (moved == node)
        {
            return project;
        }

        context.Changed(node.Id);
        return CompHelp.Replace(project, found, found.Graph.Replace(moved), context);
    }
}

/// <summary>Shows a comp node in the program monitor.</summary>
public sealed class ViewCompNodeHandler : ICommandHandler<ViewCompNodeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ViewCompNodeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (command.NodeId is { Length: > 0 } nodeId)
        {
            _ = CompHelp.Node(project, nodeId);
        }

        return PlaybackHelp.Drive(project, context, playback => playback.CompView = command.NodeId is { Length: > 0 } ? command.NodeId : null);
    }
}

/// <summary>Describes a comp graph as text.</summary>
public sealed class DescribeCompHandler : IQueryHandler<DescribeCompQuery, string>
{
    /// <inheritdoc />
    public string Handle(Project project, DescribeCompQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);

        CompHelp.Found found = CompHelp.Graph(project, query.Target);
        CompGraph graph = found.Graph;
        var text = new StringBuilder();
        string owner = found.Holder.Clip is { } clip ? $" on '{clip.Name}'" : string.Empty;
        text.Append(CultureInfo.InvariantCulture, $"Comp graph {found.Holder.Id}{owner}: {graph.Nodes.Length} nodes.").AppendLine();

        IReadOnlyList<CompNode>? order = graph.Order();
        if (graph.OutputNode is null)
        {
            text.AppendLine("It has no output, so the clip shows its picture unchanged.");
        }
        else if (order is null)
        {
            text.AppendLine("It goes round in a circle or reads a node it does not have, so the clip shows its picture unchanged.");
        }

        void Line(CompNode node)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {CompHelp.Name(node)} {node.Id}");
            if (!node.Effect.Enabled)
            {
                text.Append(" (off)");
            }

            if (node.Inputs.Length > 0)
            {
                text.Append(" <- ").Append(string.Join(", ", node.Inputs.Select(input => $"{input.Port}: {(graph.Node(input.From) is { } from ? $"{CompHelp.Name(from)} {from.Id}" : $"missing {input.From}")}")));
            }

            if (node.Effect.Parameters.Length > 0)
            {
                text.Append(" [").Append(string.Join(", ", node.Effect.Parameters.Select(parameter => $"{parameter.Name} {Value(parameter.Value)}"))).Append(']');
            }

            text.AppendLine();
        }

        HashSet<string> used = new(StringComparer.Ordinal);
        if (order is not null)
        {
            text.AppendLine("Drawn, from the picture coming in to the output:");
            foreach (CompNode node in order)
            {
                used.Add(node.Id);
                Line(node);
            }
        }

        CompNode[] unused = [.. graph.Nodes.Where(node => !used.Contains(node.Id))];
        if (unused.Length > 0)
        {
            text.AppendLine("Not reaching the output:");
            foreach (CompNode node in unused)
            {
                Line(node);
            }
        }

        return text.ToString();
    }

    private static string Value(AnimatedValue value) => value switch
    {
        StaticValue { Value: ParamValue.Text words } => $"\"{words.Value}\"",
        StaticValue constant => ParamValues.Format(constant.Value),
        _ => "animated",
    };
}
