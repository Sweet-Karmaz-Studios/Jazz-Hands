using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JazzHands.App.Services;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Selection;

namespace JazzHands.App.ViewModels.Timeline;

/// <summary>
/// The timeline tabs: one per sequence, and which one is in front.
/// </summary>
/// <remarks>
/// Kept in step with the project after every command: a sequence created from the CLI gets a tab,
/// one removed loses it. The tab in front follows the project's active sequence, and bringing a
/// tab to the front sends <c>sequence.set-active</c>, so the preview, the CLI and MCP agree about
/// which sequence is being edited.
/// </remarks>
public sealed partial class TimelineDocuments : ObservableObject
{
    private readonly ISession _session;
    private readonly SelectionService _selection;
    private readonly IUiDispatcher _ui;
    private readonly IPreviewEngine? _preview;
    private TimelineViewModel? _active;
    private bool _syncQueued;

    /// <summary>Creates the tabs for the project as it is.</summary>
    public TimelineDocuments(ISession session, SelectionService selection, IUiDispatcher ui, IPreviewEngine? preview = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        _selection = selection;
        _ui = ui;
        _preview = preview;

        _session.ProjectChanged += (_, _) => QueueSync();
        Sync();
    }

    /// <summary>One timeline per sequence, in the project's order.</summary>
    public ObservableCollection<TimelineViewModel> Documents { get; } = [];

    /// <summary>
    /// The tab in front. Typed as object because the docking manager's active content can be any
    /// panel; setting it to something that is not a timeline changes nothing here.
    /// </summary>
    public object? Active
    {
        get => _active;
        set
        {
            if (value is not TimelineViewModel timeline || ReferenceEquals(timeline, _active))
            {
                return;
            }

            _active = timeline;
            OnPropertyChanged();

            if (!string.Equals(_session.Project.ActiveSequenceId, timeline.SequenceId, StringComparison.Ordinal))
            {
                _ = timeline.RunAsync(new SetActiveSequenceCommand(timeline.SequenceId));
            }
        }
    }

    /// <summary>The timeline in front, typed.</summary>
    public TimelineViewModel? ActiveTimeline => _active;

    private void QueueSync()
    {
        if (_syncQueued)
        {
            return;
        }

        _syncQueued = true;
        _ui.Post(() =>
        {
            _syncQueued = false;
            Sync();
        });
    }

    private void Sync()
    {
        Project project = _session.Project;
        var wanted = project.Sequences.Select(sequence => sequence.Id).ToList();

        for (int index = Documents.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(Documents[index].SequenceId, StringComparer.Ordinal))
            {
                Documents.RemoveAt(index);
            }
        }

        for (int index = 0; index < wanted.Count; index++)
        {
            string id = wanted[index];
            int at = IndexOf(id);

            if (at < 0)
            {
                Documents.Insert(index, new TimelineViewModel(_session, id, _selection, _ui, _preview));
            }
            else if (at != index)
            {
                Documents.Move(at, index);
            }
        }

        string? activeId = project.ActiveSequence?.Id;
        TimelineViewModel? front = Documents.FirstOrDefault(document => string.Equals(document.SequenceId, activeId, StringComparison.Ordinal));

        if (!ReferenceEquals(front, _active))
        {
            _active = front;
            OnPropertyChanged(nameof(Active));
            OnPropertyChanged(nameof(ActiveTimeline));
        }
    }

    private int IndexOf(string sequenceId)
    {
        for (int index = 0; index < Documents.Count; index++)
        {
            if (string.Equals(Documents[index].SequenceId, sequenceId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
