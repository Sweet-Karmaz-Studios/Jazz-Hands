using System.Collections.Immutable;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Selection;

/// <summary>Raised when the selection changes.</summary>
/// <param name="Ids">What is selected now, in the order it was selected.</param>
public sealed class SelectionChangedEventArgs(ImmutableArray<string> ids) : EventArgs
{
    /// <summary>What is selected now.</summary>
    public ImmutableArray<string> Ids { get; } = ids;
}

/// <summary>
/// What the editor is pointing at: the selected clips and markers of the active sequence.
/// </summary>
/// <remarks>
/// One per session, shared by the timeline, the inspector and every remote client, and changed
/// only through <c>selection.set</c> and <c>selection.clear</c> so that a click, a CLI script and
/// an MCP call select the same way. <see cref="Changed"/> is what the control server forwards as
/// <c>selection.changed</c>.
///
/// Ids that stop existing drop out on their own: <see cref="Attach"/> prunes after every command,
/// so deleting a selected clip does not leave a ghost selected.
///
/// Thread safe: commands arrive on the dispatcher's thread and the UI reads from its own.
/// </remarks>
public sealed class SelectionService
{
    private readonly Lock _gate = new();
    private ImmutableArray<string> _ids = [];
    private Session? _session;

    /// <summary>Raised after every change, on the thread that made it.</summary>
    public event EventHandler<SelectionChangedEventArgs>? Changed;

    /// <summary>What is selected, in the order it was selected.</summary>
    public ImmutableArray<string> Ids
    {
        get
        {
            lock (_gate)
            {
                return _ids;
            }
        }
    }

    /// <summary>True when an id is selected.</summary>
    public bool Contains(string id) => Ids.Contains(id, StringComparer.Ordinal);

    /// <summary>Changes the selection.</summary>
    public void Set(IEnumerable<string> ids, SelectMode mode = SelectMode.Replace)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Update(current => Combine(current, [.. ids.Distinct(StringComparer.Ordinal)], mode));
    }

    /// <summary>Selects nothing.</summary>
    public void Clear() => Update(_ => []);

    /// <summary>Drops every id that is not a clip, transition, marker or comp node of the project's active sequence.</summary>
    public void Prune(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        HashSet<string> present = Present(project);
        Update(current => [.. current.Where(present.Contains)]);
    }

    /// <summary>Prunes after every command the session runs.</summary>
    public void Attach(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _session?.ProjectChanged -= OnProjectChanged;

        _session = session;
        session.ProjectChanged += OnProjectChanged;
    }

    /// <summary>
    /// The ids a selection may name: tracks, clips, transitions and markers of the active sequence,
    /// and the nodes of its clips' comp graphs, inside groups too (Phase 49a), which the Nodes panel
    /// selects to show in the Inspector.
    /// </summary>
    internal static HashSet<string> Present(Project project)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);

        if (project.ActiveSequence is not { } sequence)
        {
            return present;
        }

        foreach (Track track in sequence.Tracks)
        {
            // A track is selected to show its own effects in the Inspector.
            present.Add(track.Id);
            foreach (Clip clip in track.Clips)
            {
                present.Add(clip.Id);
                foreach (Effect effect in clip.Effects)
                {
                    if (effect.Comp is { } comp)
                    {
                        AddNodes(comp, present);
                    }
                }
            }

            foreach (Transition transition in track.Transitions)
            {
                present.Add(transition.Id);
            }
        }

        foreach (Marker marker in sequence.Markers)
        {
            present.Add(marker.Id);
        }

        return present;
    }

    private static void AddNodes(CompGraph comp, HashSet<string> present)
    {
        foreach (CompNode node in comp.Nodes)
        {
            present.Add(node.Id);
            if (node.Effect.Comp is { } inner)
            {
                AddNodes(inner, present);
            }
        }
    }

    private static ImmutableArray<string> Combine(ImmutableArray<string> current, string[] ids, SelectMode mode) => mode switch
    {
        SelectMode.Replace => [.. ids],
        SelectMode.Add => [.. current, .. ids.Where(id => !current.Contains(id, StringComparer.Ordinal))],
        SelectMode.Remove => [.. current.Where(id => !ids.Contains(id, StringComparer.Ordinal))],
        SelectMode.Toggle =>
        [
            .. current.Where(id => !ids.Contains(id, StringComparer.Ordinal)),
            .. ids.Where(id => !current.Contains(id, StringComparer.Ordinal)),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a selection mode."),
    };

    private void OnProjectChanged(object? sender, ProjectChangedEventArgs e) => Prune(e.Project);

    private void Update(Func<ImmutableArray<string>, ImmutableArray<string>> change)
    {
        ImmutableArray<string> after;

        lock (_gate)
        {
            after = change(_ids);
            if (after.SequenceEqual(_ids, StringComparer.Ordinal))
            {
                return;
            }

            _ids = after;
        }

        Changed?.Invoke(this, new SelectionChangedEventArgs(after));
    }
}
