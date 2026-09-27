using System.Globalization;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>What the colour graph handlers share: finding graphs and nodes, and storing a graph back.</summary>
internal static class GraphHelp
{
    /// <summary>A node, with the graph effect it is in and where that lives, or a refusal.</summary>
    internal static (ParamOwner Owner, Effect Holder, GradeGraph Graph, GradeNode Node) Node(Project project, string nodeId)
    {
        ParamOwner owner = ParamTargets.Find(project, nodeId) is { Kind: ParamOwnerKind.Effect, Graph: { Graph: { } graph } holder } found
            ? found
            : throw new CommandException("node-not-found", $"No node of a colour graph has id '{nodeId}'. 'jazz effect get <graph>' lists a graph's nodes.");

        HandlerHelp.RequireUnlocked(owner.Track);
        Effect graphEffect = owner.Graph!;
        return (owner, graphEffect, graphEffect.Graph!, graphEffect.Graph!.Node(nodeId)!);
    }

    /// <summary>The project with a graph effect's nodes replaced where it is.</summary>
    internal static Project Store(Project project, ParamOwner owner, Effect holder, GradeGraph graph, HandlerContext context)
    {
        context.Changed(holder.Id);
        context.Changed(owner.Clip?.Id ?? owner.Track.Id);
        return ParamTargets.ReplaceEffect(project, owner with { Graph = null, Effect = holder }, holder with { Graph = graph });
    }

    /// <summary>A node's type from its short name (wheels) or its effect type (color.wheels), or a refusal.</summary>
    internal static string NodeType(string type)
    {
        string full = type.Contains('.', StringComparison.Ordinal) ? type : "color." + type;
        return GradeGraph.NodeTypes.Contains(full)
            ? full
            : throw new CommandException("unknown-node-type", $"A node can be wheels, curves, hsl, lut or white-balance; '{type}' is none of them.");
    }

    /// <summary>The node, refusing one that is not in the graph.</summary>
    internal static GradeNode Require(GradeGraph graph, string id, string holderId) =>
        graph.Node(id) ?? throw new CommandException("node-not-found", $"Colour graph '{holderId}' has no node '{id}'.");

    /// <summary>The graph with every reader of one node reading another instead (inputs only, not keys).</summary>
    internal static GradeGraph Redirect(GradeGraph graph, string from, string? to, string? except = null)
    {
        EquatableArray<GradeNode> nodes = new([.. graph.Nodes.Select(node =>
        {
            if (node.Id == except || !node.Inputs.Contains(from))
            {
                return node;
            }

            string[] inputs = [.. node.Inputs.Select(id => id == from ? to : id).OfType<string>()];
            return node with { Inputs = new EquatableArray<string>(inputs) };
        })]);

        return graph with
        {
            Nodes = nodes,
            Output = graph.Output == from ? to : graph.Output,
        };
    }
}

/// <summary>Adds a node to a colour graph.</summary>
public sealed class AddColorNodeHandler : ICommandHandler<AddColorNodeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddColorNodeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        string type = GraphHelp.NodeType(command.Type);
        (ParamOwner owner, Effect holder) = Holder(ref project, command.Target, context);
        GradeGraph graph = holder.Graph ?? GradeGraph.Empty;

        // The node shown is named from here on, so a node added at the end does not take its place.
        graph = graph with { Output = graph.OutputNode?.Id };

        string id = HandlerHelp.IdOr(command.NodeId);
        HandlerHelp.RequireUnused(project, id);
        var node = new GradeNode(Effect.Create(type) with { Id = id });

        if (command.After is not null && command.ParallelTo is not null)
        {
            throw new CommandException("conflicting-options", "A node goes after another or beside it, not both: give --after or --parallel-to.");
        }

        if (command.ParallelTo is { } besideId)
        {
            GradeNode beside = GraphHelp.Require(graph, besideId, holder.Id);
            if (beside.IsMix)
            {
                throw new CommandException("invalid-value", "A node goes beside a correction, not beside a mix; add it beside one of the mix's inputs.");
            }

            node = node with { Inputs = beside.Inputs };
            GradeNode? mix = graph.Readers(beside.Id).FirstOrDefault(reader => reader.IsMix && reader.Inputs.Length < GradeGraph.MostInputs);
            if (mix is not null)
            {
                // Into the mix it already feeds, with an equal share when the shares were given.
                GradeNode joined = mix with
                {
                    Inputs = mix.Inputs.Add(id),
                    Weights = mix.Weights.IsEmpty ? mix.Weights : mix.Weights.Add(1),
                };
                graph = graph.Replace(joined) with { Nodes = graph.Replace(joined).Nodes.Add(node) };
            }
            else
            {
                // A new mix of the two takes the place of the node it sits beside.
                var mixer = new GradeNode(Effect.Create(GradeGraph.MixType) with { Id = Id.New() }, new EquatableArray<string>([beside.Id, id]));
                graph = GraphHelp.Redirect(graph, beside.Id, mixer.Id);
                graph = graph with { Nodes = graph.Nodes.Add(node).Add(mixer) };
            }
        }
        else if (command.After is { } afterId)
        {
            GradeNode after = GraphHelp.Require(graph, afterId, holder.Id);
            node = node with { Inputs = [after.Id] };
            graph = GraphHelp.Redirect(graph, after.Id, id);
            graph = graph with { Nodes = graph.Nodes.Add(node) };
        }
        else
        {
            // After the output: the new node is the last, and shows.
            GradeNode? output = graph.OutputNode;
            node = node with { Inputs = output is null ? [] : [output.Id] };
            graph = graph with { Nodes = graph.Nodes.Add(node), Output = id };
        }

        context.Changed(id);
        return GraphHelp.Store(project, owner, holder, graph, context);
    }

    /// <summary>The graph effect the node goes into: the one named, or a clip's first, added when it has none.</summary>
    private static (ParamOwner Owner, Effect Holder) Holder(ref Project project, string target, HandlerContext context)
    {
        ParamOwner found = ParamHelp.Owner(project, target);
        HandlerHelp.RequireUnlocked(found.Track);

        if (found is { Kind: ParamOwnerKind.Effect, Graph: null, Effect: { } effect })
        {
            return effect.TypeId == GradeGraph.TypeId
                ? (found, effect)
                : throw new CommandException("not-a-graph", $"Effect '{target}' is a '{effect.TypeId}', not a colour graph; 'jazz color to-graph {target}' makes it one.");
        }

        if (found.Kind is not (ParamOwnerKind.Clip or ParamOwnerKind.Track) || found.Track.Kind is TrackKind.Audio or TrackKind.Subtitle)
        {
            throw new CommandException("not-a-graph", $"'{target}' is {ParamHelp.Describe(found)}; a node goes into a colour graph effect, or a picture clip or track.");
        }

        EquatableArray<Effect> chain = EffectHelp.Chain(found);
        if (chain.FirstOrDefault(item => item.TypeId == GradeGraph.TypeId) is { } existing)
        {
            return (found with { Kind = ParamOwnerKind.Effect, Effect = existing }, existing);
        }

        Effect added = Effect.Create(GradeGraph.TypeId) with { Graph = GradeGraph.Empty };
        project = EffectHelp.WithChain(project, found, chain.Add(added));
        context.Changed(added.Id);
        ParamOwner owner = ParamTargets.Find(project, added.Id)!;
        return (owner, owner.Effect!);
    }
}

/// <summary>Connects one node of a colour graph to another.</summary>
public sealed class ConnectColorNodeHandler : ICommandHandler<ConnectColorNodeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ConnectColorNodeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (ParamOwner owner, Effect holder, GradeGraph graph, GradeNode node) = GraphHelp.Node(project, command.NodeId);
        GradeNode? from = command.From is { } fromId ? GraphHelp.Require(graph, fromId, holder.Id) : null;

        if (from is not null && (from.Id == node.Id || graph.Reads(from.Id, node.Id)))
        {
            throw new CommandException("would-cycle", $"Node '{from.Id}' already reads '{node.Id}', so '{node.Id}' reading it would go round in a circle.");
        }

        GradeNode connected;
        if (command.Key)
        {
            if (from is not { Effect.TypeId: "color.hsl" })
            {
                throw new CommandException("not-a-qualifier", "A key comes from an HSL qualifier node: give its id with --from.");
            }

            connected = node with { Key = from.Id };
        }
        else if (node.IsMix)
        {
            if (from is null)
            {
                throw new CommandException("invalid-value", "A mix reads nodes: give the one it reads with --from.");
            }

            int slot = (command.Input ?? node.Inputs.Length + 1) - 1;
            if (slot < 0 || slot > node.Inputs.Length || slot >= GradeGraph.MostInputs)
            {
                throw new CommandException("value-out-of-range", $"This mix has {node.Inputs.Length} inputs and takes at most {GradeGraph.MostInputs}; --input is from 1 to {Math.Min(node.Inputs.Length + 1, GradeGraph.MostInputs)}.");
            }

            connected = slot == node.Inputs.Length
                ? node with { Inputs = node.Inputs.Add(from.Id), Weights = node.Weights.IsEmpty ? node.Weights : node.Weights.Add(1) }
                : node with { Inputs = node.Inputs.SetItem(slot, from.Id) };
        }
        else
        {
            if (command.Input is > 1)
            {
                throw new CommandException("value-out-of-range", "Only a mix has more than one input.");
            }

            connected = node with { Inputs = from is null ? [] : [from.Id] };
        }

        if (connected == node)
        {
            return project;
        }

        context.Changed(node.Id);
        return GraphHelp.Store(project, owner, holder, graph.Replace(connected), context);
    }
}

/// <summary>Removes a node from a colour graph.</summary>
public sealed class RemoveColorNodeHandler : ICommandHandler<RemoveColorNodeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveColorNodeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (ParamOwner owner, Effect holder, GradeGraph graph, GradeNode node) = GraphHelp.Node(project, command.NodeId);

        // Whatever read it reads what it read, except a mix, which loses that input and its share.
        string? upstream = node.Inputs.Length > 0 ? node.Inputs[0] : null;
        string? shown = graph.OutputNode?.Id;
        GradeGraph rewired = graph with
        {
            Nodes = new EquatableArray<GradeNode>([.. graph.Nodes.Where(other => other.Id != node.Id).Select(other =>
            {
                GradeNode updated = other.Key == node.Id ? other with { Key = null } : other;
                if (!updated.Inputs.Contains(node.Id))
                {
                    return updated;
                }

                if (updated.IsMix)
                {
                    int slot = updated.Inputs.IndexOf(id => id == node.Id);
                    // A parallel node taken out leaves the mix; a mix's only input is replaced.
                    bool only = updated.Inputs.Length == 1 && upstream is not null;
                    return only
                        ? updated with { Inputs = updated.Inputs.SetItem(slot, upstream!) }
                        : updated with
                        {
                            Inputs = new EquatableArray<string>([.. updated.Inputs.Where((_, index) => index != slot)]),
                            Weights = updated.Weights.IsEmpty ? updated.Weights : new EquatableArray<float>([.. updated.Weights.Where((_, index) => index != slot)]),
                        };
                }

                return updated with { Inputs = upstream is null ? [] : [upstream] };
            })]),
            Output = shown == node.Id ? upstream : shown,
        };

        context.Changed(node.Id);
        return GraphHelp.Store(project, owner, holder, rewired, context);
    }
}

/// <summary>Changes a node of a colour graph.</summary>
public sealed class SetColorNodeHandler : ICommandHandler<SetColorNodeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetColorNodeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (ParamOwner owner, Effect holder, GradeGraph graph, GradeNode node) = GraphHelp.Node(project, command.NodeId);
        GradeNode changed = node;

        if (command.Weights is { } text)
        {
            if (!node.IsMix)
            {
                throw new CommandException("not-a-mix", "Only a mix has shares; set the node's own parameters with --param and --value.");
            }

            float[] weights = [.. text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(part =>
                float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float weight) && weight >= 0 && float.IsFinite(weight)
                    ? weight
                    : throw new CommandException("invalid-value", $"'{part}' is not a share; shares are numbers of nothing or more, such as 1,1."))];
            if (weights.Length != node.Inputs.Length)
            {
                throw new CommandException("invalid-value", $"This mix has {node.Inputs.Length} inputs, so it takes {node.Inputs.Length} shares; {weights.Length} were given.");
            }

            changed = changed with { Weights = new EquatableArray<float>(weights) };
        }

        if (command.NoKey)
        {
            changed = changed with { Key = null };
        }

        if (command.Enabled is { } enabled)
        {
            changed = changed with { Effect = changed.Effect with { Enabled = enabled } };
        }

        GradeGraph updated = changed == node ? graph : graph.Replace(changed);
        if (command.Output)
        {
            updated = updated with { Output = node.Id };
        }

        if (updated != graph)
        {
            context.Changed(node.Id);
            project = GraphHelp.Store(project, owner, holder, updated, context);
        }

        if (command.Param is { } name)
        {
            if (command.Value is not { } value)
            {
                throw new CommandException("missing-argument", "--param needs a --value.");
            }

            if (node.IsMix)
            {
                throw new CommandException("not-a-correction", "A mix has no parameters of its own; its shares are --weights.");
            }

            project = SetParamHandler.Set(project, ParamTargets.Find(project, node.Id)!, name, value, null, false, context);
        }
        else if (command.Value is not null)
        {
            throw new CommandException("missing-argument", "--value needs the --param it is for.");
        }

        return project;
    }
}

/// <summary>Turns a colour effect into a colour graph of one node.</summary>
public sealed class ConvertToColorGraphHandler : ICommandHandler<ConvertToColorGraphCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ConvertToColorGraphCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ParamOwner owner = EffectHelp.InChain(project, command.EffectId, "color node-set");
        HandlerHelp.RequireUnlocked(owner.Track);
        Effect effect = owner.Effect!;
        if (!GradeGraph.NodeTypes.Contains(effect.TypeId))
        {
            throw new CommandException("not-a-correction", $"Effect '{effect.Id}' is a '{effect.TypeId}'; a node graph is made of Colour Wheels, Curves, HSL qualifier, LUT and White balance.");
        }

        string id = HandlerHelp.IdOr(command.GraphId);
        HandlerHelp.RequireUnused(project, id);

        // The graph takes the effect's place, and the effect keeps its id, parameters, masks and
        // on or off as the graph's node.
        var holder = new Effect(id, GradeGraph.TypeId, Enabled: true, EquatableArray<EffectParameter>.Empty, Graph: new GradeGraph([new GradeNode(effect)]));
        EquatableArray<Effect> chain = EffectHelp.Chain(owner);
        context.Changed(id);
        context.Changed(effect.Id);
        context.Changed(owner.Clip?.Id ?? owner.Track.Id);
        return EffectHelp.WithChain(project, owner, chain.SetItem(chain.IndexOf(item => item.Id == effect.Id), holder));
    }
}
