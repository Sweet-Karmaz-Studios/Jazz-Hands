namespace JazzHands.Core.Model;

/// <summary>One wire into a node: which of its ports, and the node it comes from.</summary>
/// <param name="Port">The port's name: <c>input</c>, <c>background</c>, <c>foreground</c>, <c>mask</c>, <c>matte</c>, or a 3D render's <c>1</c> to <c>8</c>.</param>
/// <param name="From">The node whose picture it takes, by id.</param>
public sealed record CompInput(string Port, string From);

/// <summary>
/// One node of a comp graph (Phase 49): what it does, as an effect entry (so <c>param.set</c>,
/// keyframes and masks reach it by its id), the wires into its ports, and where it sits in the
/// node view.
/// </summary>
/// <param name="Effect">What it does: a <c>comp.*</c> node type, a 3D object type, or any video effect or generator.</param>
/// <param name="Inputs">The wires into it, a port at most once.</param>
/// <param name="X">Its left edge in the node view.</param>
/// <param name="Y">Its top edge in the node view.</param>
public sealed record CompNode(Effect Effect, EquatableArray<CompInput> Inputs = default, double X = 0, double Y = 0) : IEquatable<CompNode>
{
    /// <summary>The node's id, which is its effect's.</summary>
    public string Id => Effect.Id;

    /// <summary>The node wired into a port, or null.</summary>
    public string? Input(string port) => Inputs.FirstOrDefault(input => string.Equals(input.Port, port, StringComparison.Ordinal))?.From;
}

/// <summary>
/// A compositing node graph inside a clip (Phase 49), held by a <c>comp.graph</c> effect: the
/// clip's picture comes in at <c>comp.in</c> nodes, goes through merges, transforms, mattes,
/// effects, generators, other media and 3D renders, and what reaches the one <c>comp.out</c> node
/// is the clip's picture from there on.
/// </summary>
/// <remarks>
/// Nodes are kept in the order they were added. A graph with no output, or whose output reads in a
/// circle or a node it does not have, shows its picture unchanged. Nodes the output does not reach
/// are kept but not drawn.
/// </remarks>
/// <param name="Nodes">The nodes.</param>
public sealed record CompGraph(EquatableArray<CompNode> Nodes) : IEquatable<CompGraph>
{
    /// <summary>The effect type that holds a graph.</summary>
    public const string TypeId = "comp.graph";

    /// <summary>The picture coming into the graph: the clip's own, placed.</summary>
    public const string In = "comp.in";

    /// <summary>What the graph shows. One per graph.</summary>
    public const string Out = "comp.out";

    /// <summary>Another media item's picture, fitted to the frame, at a time offset.</summary>
    public const string Media = "comp.media";

    /// <summary>A foreground over a background, with a blend, an opacity, a place and an optional mask.</summary>
    public const string Merge = "comp.merge";

    /// <summary>A picture moved, scaled and turned.</summary>
    public const string Transform = "comp.transform";

    /// <summary>A picture kept only where another's alpha or brightness says.</summary>
    public const string Matte = "comp.matte";

    /// <summary>A picture as a plane in 3D, for a 3D render.</summary>
    public const string Plane = "comp.plane3d";

    /// <summary>A 3D scene of the planes, text, shapes, models, camera and lights wired into it.</summary>
    public const string Render3D = "comp.render3d";

    /// <summary>The most things a 3D render takes.</summary>
    public const int MostObjects = 8;

    /// <summary>A graph with no nodes, which shows its picture unchanged.</summary>
    public static CompGraph Empty { get; } = new(EquatableArray<CompNode>.Empty);

    /// <summary>
    /// The ports a node of a type has, in the order they are drawn. A video effect has one,
    /// <c>input</c>; a generator, media or <c>comp.in</c> none. <paramref name="isGenerator"/> says which an
    /// unknown type is, which only the effect registry knows.
    /// </summary>
    public static IReadOnlyList<string> PortsOf(string typeId, bool isGenerator) => typeId switch
    {
        In or Media => [],
        Out or Transform or Plane => ["input"],
        Merge => ["background", "foreground", "mask"],
        Matte => ["input", "matte"],
        Render3D => [.. Enumerable.Range(1, MostObjects).Select(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture))],
        _ when SceneObjects.Is(typeId) || SceneObjects.IsMesh(typeId) || isGenerator => [],
        _ => ["input"],
    };

    /// <summary>True for a type a 3D render takes: a plane, text, a shape, a model, a camera or a light.</summary>
    public static bool IsObject3D(string typeId) => typeId == Plane || SceneObjects.Is(typeId) || SceneObjects.IsMesh(typeId);

    /// <summary>The node with an id, or null.</summary>
    public CompNode? Node(string id) => Nodes.FirstOrDefault(node => node.Id == id);

    /// <summary>The graph's output node, or null when it has none.</summary>
    public CompNode? OutputNode => Nodes.FirstOrDefault(node => node.Effect.TypeId == Out);

    /// <summary>
    /// The nodes the output needs, each after the nodes it reads, ending with the output; empty for
    /// a graph with no output, and null when it goes round in a circle or reads a node it does not
    /// have.
    /// </summary>
    public IReadOnlyList<CompNode>? Order() => OutputNode is { } output ? OrderFrom(output) : [];

    /// <summary>The nodes one node needs, each after what it reads, ending with it; null for a circle or a missing node.</summary>
    public IReadOnlyList<CompNode>? OrderFrom(CompNode last)
    {
        ArgumentNullException.ThrowIfNull(last);

        var order = new List<CompNode>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        bool Visit(CompNode node)
        {
            if (done.Contains(node.Id))
            {
                return true;
            }

            if (!visiting.Add(node.Id))
            {
                return false;
            }

            foreach (CompInput input in node.Inputs)
            {
                if (Node(input.From) is not { } from || !Visit(from))
                {
                    return false;
                }
            }

            visiting.Remove(node.Id);
            done.Add(node.Id);
            order.Add(node);
            return true;
        }

        return Visit(last) ? order : null;
    }

    /// <summary>True when <paramref name="node"/> reads <paramref name="from"/>, however far back, or is it.</summary>
    public bool Reads(string node, string from)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([node]);
        while (pending.Count > 0)
        {
            string id = pending.Pop();
            if (id == from)
            {
                return true;
            }

            if (!seen.Add(id) || Node(id) is not { } current)
            {
                continue;
            }

            foreach (CompInput input in current.Inputs)
            {
                pending.Push(input.From);
            }
        }

        return false;
    }

    /// <summary>A copy with a node replaced by one with the same id.</summary>
    public CompGraph Replace(CompNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return this with { Nodes = Nodes.SetItem(Nodes.IndexOf(item => item.Id == node.Id), node) };
    }

    /// <summary>A copy without a node, and without every wire from it.</summary>
    public CompGraph Without(string id) =>
        new([.. Nodes.Where(node => node.Id != id).Select(node => node with { Inputs = [.. node.Inputs.Where(input => input.From != id)] })]);
}
