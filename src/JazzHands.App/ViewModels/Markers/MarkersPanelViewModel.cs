using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.App.ViewModels.Markers;

/// <summary>One marker in the Markers panel, its name and chapter flag editable in place.</summary>
public sealed partial class MarkerRowViewModel : ObservableObject
{
    private readonly MarkersPanelViewModel _owner;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isChapter;

    internal MarkerRowViewModel(MarkersPanelViewModel owner, MarkerInfo marker, Rational rate)
    {
        _owner = owner;
        Info = marker;
        _name = marker.Name;
        _isChapter = marker.IsChapter;
        Time = Timecode.FormatClock(marker.TimelineTime);
        Frames = Timecode.Format(marker.TimelineTime, rate);
    }

    /// <summary>What the query said.</summary>
    public MarkerInfo Info { get; }

    /// <summary>Where it sits, as a clock.</summary>
    public string Time { get; }

    /// <summary>Where it sits, as timecode.</summary>
    public string Frames { get; }

    /// <summary>Its colour, for the swatch.</summary>
    public string Color => Info.Color.Length > 0 ? Info.Color : "#E0A33C";

    /// <summary>True for a range marker.</summary>
    public bool IsRange => Info.Duration > Flicks.Zero;

    partial void OnNameChanged(string value)
    {
        if (value != Info.Name)
        {
            _ = _owner.SetAsync(this, new SetMarkerCommand(Info.Id, Name: value));
        }
    }

    partial void OnIsChapterChanged(bool value)
    {
        if (value != Info.IsChapter)
        {
            _ = _owner.SetAsync(this, new SetMarkerCommand(Info.Id, IsChapter: value));
        }
    }
}

/// <summary>
/// The Markers panel: the active sequence's markers in time order, to jump to, rename, colour
/// as chapters and remove, and a button to add one at the playhead.
/// </summary>
/// <remarks>
/// Every change is a marker command (<c>marker.add</c>, <c>marker.set</c>, <c>marker.remove</c>)
/// and every jump is <c>playback.seek</c>, so the panel does what the CLI would.
/// </remarks>
public sealed partial class MarkersPanelViewModel : ToolViewModel
{
    /// <summary>The id the layout knows the panel by.</summary>
    public const string PanelId = "markers";

    private readonly ISession _session;
    private readonly IUiDispatcher _ui;
    private readonly Func<Flicks> _playhead;
    private int _refreshQueued;
    private bool _restoring;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private MarkerRowViewModel? _selected;

    /// <summary>Creates the panel.</summary>
    /// <param name="session">Where the commands go.</param>
    /// <param name="ui">The UI thread.</param>
    /// <param name="playhead">Where a new marker goes.</param>
    public MarkersPanelViewModel(ISession session, IUiDispatcher ui, Func<Flicks>? playhead = null)
        : base(PanelId, "Markers")
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(ui);
        _session = session;
        _ui = ui;
        _playhead = playhead ?? (() => Flicks.Zero);
        _session.ProjectChanged += (_, _) =>
        {
            if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            {
                _ui.Post(() =>
                {
                    Volatile.Write(ref _refreshQueued, 0);
                    Refresh();
                });
            }
        };
        Refresh();
    }

    /// <summary>The markers, in time order.</summary>
    public ObservableCollection<MarkerRowViewModel> Rows { get; } = [];

    /// <summary>Reads the markers again.</summary>
    public void Refresh()
    {
        string? keep = Selected?.Info.Id;
        Project project = _session.Project;
        Rational rate = project.ActiveSequence is { } sequence ? project.SettingsFor(sequence).FrameRate : project.Settings.FrameRate;
        MarkerInfo[] markers = project.ActiveSequence is null ? [] : _session.Query(new ListMarkersQuery());
        Rows.Clear();
        foreach (MarkerInfo marker in markers.OrderBy(marker => marker.TimelineTime))
        {
            Rows.Add(new MarkerRowViewModel(this, marker, rate));
        }

        // Keeping the selection across a refresh is not a click: no seek.
        _restoring = true;
        Selected = Rows.FirstOrDefault(row => row.Info.Id == keep);
        _restoring = false;
        Status = Rows.Count == 0 ? "No markers. Add one at the playhead, or press M." : string.Empty;
    }

    internal async Task SetAsync(MarkerRowViewModel row, ICommand command)
    {
        CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(true);
        if (!result.Ok)
        {
            Status = result.Error ?? "That marker could not be changed.";
            Refresh();
        }
    }

    /// <summary>Moves the playhead to a marker.</summary>
    [RelayCommand]
    private Task JumpAsync(MarkerRowViewModel? row) =>
        row is null ? Task.CompletedTask : Run(new SeekCommand(row.Info.TimelineTime));

    /// <summary>Adds a marker where the playhead is.</summary>
    [RelayCommand]
    private Task AddAsync() => Run(new AddMarkerCommand(_playhead(), $"Marker {Rows.Count + 1}"));

    /// <summary>Removes a marker.</summary>
    [RelayCommand]
    private Task RemoveAsync(MarkerRowViewModel? row) =>
        row is null ? Task.CompletedTask : Run(new RemoveMarkerCommand(row.Info.Id));

    partial void OnSelectedChanged(MarkerRowViewModel? value)
    {
        if (value is not null && !_restoring)
        {
            _ = Run(new SeekCommand(value.Info.TimelineTime));
        }
    }

    private async Task Run(ICommand command)
    {
        CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(true);
        if (!result.Ok)
        {
            Status = result.Error ?? "That did not work.";
        }
    }
}
