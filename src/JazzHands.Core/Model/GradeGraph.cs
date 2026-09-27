namespace JazzHands.Core.Model;

/// <summary>
/// One node of a colour graph: a correction (an ordinary colour effect, with its own parameters
/// and masks) and where its picture comes from.
/// </summary>
/// <remarks>
/// The node's id is its effect's id, so <c>param.set</c>, keyframes and masks reach a node the
/// way they reach any effect. A mix node's effect is of type <see cref="GradeGraph.MixType"/>,
/// which has no parameters of its own; its <paramref name="Weights"/> say how much of each input
/// it takes.
/// </remarks>
/// <param name="Effect">The correction: color.wheels, color.curves, color.hsl, color.lut or color.white-balance, or a mix.</param>
/// <param name="Inputs">The nodes it reads, by id; none for the picture coming into the graph. A mix reads two to four.</param>
/// <param name="Key">An HSL qualifier node whose matte limits where this node applies, or null for everywhere (within its masks).</param>
/// <param name="Weights">A mix node's share of each input, in the order of <paramref name="Inputs"/>; equal shares when empty.</param>
public sealed record GradeNode(
    Effect Effect,
    EquatableArray<string> Inputs = default,
    string? Key = null,
    EquatableArray<float> Weights = default) : IEquatable<GradeNode>
{
    /// <summary>The node's id, which is its effect's.</summary>
    public string Id => Effect.Id;

    /// <summary>True for a parallel mixer.</summary>
    public bool IsMix => Effect.TypeId == GradeGraph.MixType;

    /// <summary>The share of an input, normalised so a mix's shares add up to one.</summary>
    public float Share(int input)
    {
        if (Inputs.Length == 0)
        {
            return 0;
        }

        float Weight(int index) => index < Weights.Length ? Math.Max(Weights[index], 0) : 1;

        float total = 0;
        for (int index = 0; index < Inputs.Length; index++)
        {
            total += Weight(index);
        }

        return total <= 0 ? 1f / Inputs.Length : Weight(input) / total;
    }
}

/// <summary>
/// A grade as a small graph of nodes (Phase 44), the way a colourist builds one in Resolve: nodes
/// one after another (serial), or side by side from the same picture and mixed (parallel), each
/// limited by its own masks or by a qualifier's key. Held by a <c>color.graph</c> effect.
/// </summary>
/// <remarks>
/// Nodes are kept in the order they were added. What the graph shows is its output node's
/// picture: <see cref="Output"/> when set, otherwise the last node. Nodes nothing reaches from the
/// output are kept but not drawn, so a node can be taken out of the grade and put back.
/// </remarks>
/// <param name="Nodes">The nodes.</param>
/// <param name="Output">The node whose picture the graph shows; the last node when null.</param>
public sealed record GradeGraph(EquatableArray<GradeNode> Nodes, string? Output = null) : IEquatable<GradeGraph>
{
    /// <summary>The effect type of a graph.</summary>
    public const string TypeId = "color.graph";

    /// <summary>The effect type of a parallel mixer node.</summary>
    public const string MixType = "color.mix";

    /// <summary>The corrections a node can be.</summary>
    public static IReadOnlyList<string> NodeTypes { get; } = ["color.wheels", "color.curves", "color.hsl", "color.lut", "color.white-balance"];

    /// <summary>The most inputs a mix takes.</summary>
    public const int MostInputs = 4;

    /// <summary>A graph with no nodes, which shows its picture unchanged.</summary>
    public static GradeGraph Empty { get; } = new(EquatableArray<GradeNode>.Empty);

    /// <summary>The node with an id, or null.</summary>
    public GradeNode? Node(string id) => Nodes.FirstOrDefault(node => node.Id == id);

    /// <summary>The node the graph shows, or null when it has none.</summary>
    public GradeNode? OutputNode => Output is { } id ? Node(id) : Nodes.Length > 0 ? Nodes[^1] : null;

    /// <summary>
    /// The nodes the output needs, each after the nodes it reads (and its key), ending with the
    /// output; empty for an empty graph, and null when the graph goes round in a circle or reads
    /// a node it does not have.
    /// </summary>
    public IReadOnlyList<GradeNode>? Order()
    {
        if (OutputNode is not { } output)
        {
            return [];
        }

        var order = new List<GradeNode>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        bool Visit(GradeNode node)
        {
            if (done.Contains(node.Id))
            {
                return true;
            }

            if (!visiting.Add(node.Id))
            {
                return false;
            }

            foreach (string id in node.Key is { } key ? [.. node.Inputs, key] : node.Inputs.AsEnumerable())
            {
                if (Node(id) is not { } input || !Visit(input))
                {
                    return false;
                }
            }

            visiting.Remove(node.Id);
            done.Add(node.Id);
            order.Add(node);
            return true;
        }

        return Visit(output) ? order : null;
    }

    /// <summary>True when <paramref name="node"/> reads <paramref name="from"/>, however far back.</summary>
    public bool Reads(string node, string from)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([node]);
        while (pending.Count > 0)
        {
            string id = pending.Pop();
            if (!seen.Add(id) || Node(id) is not { } current)
            {
                continue;
            }

            foreach (string input in current.Key is { } key ? [.. current.Inputs, key] : current.Inputs.AsEnumerable())
            {
                if (input == from)
                {
                    return true;
                }

                pending.Push(input);
            }
        }

        return false;
    }

    /// <summary>The nodes that read a node, as an input or a key.</summary>
    public IEnumerable<GradeNode> Readers(string id) => Nodes.Where(node => node.Inputs.Contains(id) || node.Key == id);

    /// <summary>A copy with a node replaced by one with the same id.</summary>
    public GradeGraph Replace(GradeNode node) =>
        this with { Nodes = Nodes.SetItem(Nodes.IndexOf(item => item.Id == node.Id), node) };
}
