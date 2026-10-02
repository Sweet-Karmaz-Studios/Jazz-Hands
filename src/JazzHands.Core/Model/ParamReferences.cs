namespace JazzHands.Core.Model;

/// <summary>What a parameter that names something outside the project's effects holds.</summary>
public enum ParamReferenceKind
{
    /// <summary>A media item, by id.</summary>
    Media,

    /// <summary>A file, by path relative to the project (or absolute).</summary>
    File,
}

/// <summary>One parameter that names a media item or a file.</summary>
/// <param name="Clip">The clip whose effect (or comp or colour graph node) holds it.</param>
/// <param name="Effect">The effect or node.</param>
/// <param name="Param">The parameter's name.</param>
/// <param name="Kind">What it names.</param>
/// <param name="Value">The media id or path.</param>
public sealed record ParamReference(Clip Clip, Effect Effect, string Param, ParamReferenceKind Kind, string Value);

/// <summary>
/// The parameters that point outside an effect: a comp graph's media nodes and an environment
/// light's still (media ids), and a 3D model's file (a path). A clip's media is its <c>MediaId</c>;
/// these are the other uses, which consolidating, archiving and removing media must know about.
/// </summary>
public static class ParamReferences
{
    /// <summary>Which effect types hold which references, in which parameter.</summary>
    public static IReadOnlyList<(string TypeId, string Param, ParamReferenceKind Kind)> Kinds { get; } =
    [
        (CompGraph.Media, "media", ParamReferenceKind.Media),
        (SceneObjects.Light, "image", ParamReferenceKind.Media),
        (SceneObjects.Model, "file", ParamReferenceKind.File),
    ];

    /// <summary>Every reference in the project, sequence by sequence, set ones only, nodes inside graphs included.</summary>
    public static IEnumerable<ParamReference> All(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (Clip clip in project.Sequences.SelectMany(sequence => sequence.Tracks).SelectMany(track => track.Clips))
        {
            foreach (Effect effect in clip.Effects.SelectMany(Within))
            {
                foreach ((string typeId, string param, ParamReferenceKind kind) in Kinds)
                {
                    if (effect.TypeId == typeId && Text(effect, param) is { Length: > 0 } value)
                    {
                        yield return new ParamReference(clip, effect, param, kind, value);
                    }
                }
            }
        }
    }

    /// <summary>Every media id a parameter names.</summary>
    public static HashSet<string> MediaIds(Project project) =>
        new(All(project).Where(reference => reference.Kind == ParamReferenceKind.Media).Select(reference => reference.Value), StringComparer.Ordinal);

    /// <summary>
    /// A copy with each reference's value replaced by what <paramref name="change"/> gives (null
    /// keeps it), wherever it is: a clip's effects or the nodes of its graphs.
    /// </summary>
    public static Project Replace(Project project, Func<ParamReference, string?> change)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(change);

        return project with
        {
            Sequences = [.. project.Sequences.Select(sequence => sequence with
            {
                Tracks = [.. sequence.Tracks.Select(track => track with
                {
                    Clips = [.. track.Clips.Select(clip => clip with { Effects = [.. clip.Effects.Select(effect => Map(clip, effect, change))] })],
                })],
            })],
        };
    }

    /// <summary>An effect and every node inside its graphs, however deep.</summary>
    private static IEnumerable<Effect> Within(Effect effect)
    {
        yield return effect;
        foreach (Effect node in (effect.Comp?.Nodes.Select(node => node.Effect) ?? []).Concat(effect.Graph?.Nodes.Select(node => node.Effect) ?? []).SelectMany(Within))
        {
            yield return node;
        }
    }

    private static Effect Map(Clip clip, Effect effect, Func<ParamReference, string?> change)
    {
        Effect mapped = effect with
        {
            Comp = effect.Comp is { } comp ? new CompGraph([.. comp.Nodes.Select(node => node with { Effect = Map(clip, node.Effect, change) })]) : null,
            Graph = effect.Graph is { } graph ? graph with { Nodes = [.. graph.Nodes.Select(node => node with { Effect = Map(clip, node.Effect, change) })] } : null,
        };

        foreach ((string typeId, string param, ParamReferenceKind kind) in Kinds)
        {
            if (mapped.TypeId == typeId && Text(mapped, param) is { Length: > 0 } value && change(new ParamReference(clip, mapped, param, kind, value)) is { } replaced && replaced != value)
            {
                mapped = mapped.WithParameter(param, AnimatedValue.Constant(new ParamValue.Text(replaced)));
            }
        }

        return mapped;
    }

    private static string? Text(Effect effect, string param) =>
        effect.Parameter(param) is StaticValue { Value: ParamValue.Text { Value: var text } } ? text.Trim() : null;
}
