using System.Collections.Immutable;
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
    private readonly IDialogService? _dialogs;
    private readonly IClipboardService? _clipboard;
    private readonly Controls.Timeline.ITimelineImagery _imagery;
    private TimelineViewModel? _active;
    private bool _syncQueued;
    private ImmutableArray<string> _trail = [];

    /// <summary>Creates the tabs for the project as it is.</summary>
    public TimelineDocuments(
        ISession session,
        SelectionService selection,
        IUiDispatcher ui,
        IPreviewEngine? preview = null,
        IDialogService? dialogs = null,
        IClipboardService? clipboard = null,
        Controls.Timeline.ITimelineImagery? imagery = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        _selection = selection;
        _ui = ui;
        _preview = preview;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _imagery = imagery ?? Controls.Timeline.NoImagery.Instance;

        _session.ProjectChanged += (_, _) => QueueSync();
        Sync();
    }

    /// <summary>The tool in hand and snapping, the same in every tab.</summary>
    public TimelineTools Tools { get; } = new();

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

            UpdateTrails();
        }
    }

    /// <summary>The timeline in front, typed.</summary>
    public TimelineViewModel? ActiveTimeline => _active;

    /// <summary>The sequences opened one inside the other, outermost first.</summary>
    public ImmutableArray<string> Trail => _trail;

    /// <summary>
    /// Brings a sequence's tab to the front. Opened from a compound clip in another sequence, it
    /// goes on the trail after that one, so the breadcrumb leads back; opened from the trail, the
    /// trail is cut back to it.
    /// </summary>
    /// <param name="sequenceId">The sequence to show.</param>
    /// <param name="fromSequenceId">The sequence whose compound clip opened it, or null.</param>
    public void Open(string sequenceId, string? fromSequenceId)
    {
        ArgumentNullException.ThrowIfNull(sequenceId);

        if (fromSequenceId is not null)
        {
            int from = _trail.IndexOf(fromSequenceId);
            _trail = (from >= 0 ? _trail[..(from + 1)] : [fromSequenceId]).Add(sequenceId);
        }
        else if (_trail.IndexOf(sequenceId) is var at and >= 0)
        {
            _trail = _trail[..(at + 1)];
        }

        if (Documents.FirstOrDefault(document => string.Equals(document.SequenceId, sequenceId, StringComparison.Ordinal)) is { } document)
        {
            Active = document;
        }

        UpdateTrails();
    }

    private void UpdateTrails()
    {
        // A tab picked by hand that is not on the trail ends the trail.
        if (_active is not null && !_trail.Contains(_active.SequenceId))
        {
            _trail = [];
        }

        Project project = _session.Project;
        foreach (TimelineViewModel document in Documents)
        {
            int at = _trail.IndexOf(document.SequenceId);
            document.SetTrail(at < 0 || _trail.Length < 2
                ? []
                : [.. _trail[..(at + 1)].Select((id, index) => new SequenceCrumb(id, project.Sequence(id)?.Name ?? id, index == at))]);
        }
    }

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
                Documents.Insert(index, new TimelineViewModel(_session, id, _selection, _ui, _preview, _dialogs, Tools)
                {
                    Clipboard = _clipboard,
                    Imagery = _imagery,
                    OpenSequence = Open,
                });
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

        UpdateTrails();
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
