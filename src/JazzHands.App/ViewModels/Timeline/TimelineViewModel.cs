using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Controls.Timeline;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Playback;
using JazzHands.Engine.Selection;
using Serilog;

namespace JazzHands.App.ViewModels.Timeline;

/// <summary>Which parts of the timeline have to be drawn again.</summary>
[Flags]
public enum TimelineLayers
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Track lanes and the in and out shading.</summary>
    Lanes = 1,

    /// <summary>Clip bodies and names.</summary>
    Clips = 2,

    /// <summary>The marker lane and marker lines.</summary>
    Markers = 4,

    /// <summary>The timecode ruler.</summary>
    Ruler = 8,

    /// <summary>Selected clip outlines and the selection box.</summary>
    Selection = 16,

    /// <summary>Where a drag, trim or drop would put things.</summary>
    Ghost = 32,

    /// <summary>The playhead.</summary>
    Playhead = 64,

    /// <summary>Everything: a zoom, a scroll, a new snapshot.</summary>
    All = Lanes | Clips | Markers | Ruler | Selection | Ghost | Playhead,
}

/// <summary>
/// One sequence's timeline: what is on it, where the view is, what is selected, and what the
/// mouse is doing to it.
/// </summary>
/// <remarks>
/// A document tab, one per sequence. It never changes the project: every edit it makes is a
/// command through <see cref="ISession"/>, the same command the CLI and MCP send, and what it
/// shows is rebuilt from the snapshot that comes back. The selection likewise is the session's
/// <see cref="SelectionService"/>, changed with <c>selection.set</c>, so a remote client selecting
/// a clip highlights it here.
///
/// The drawing is <see cref="TimelineControl"/>'s. This says what changed through
/// <see cref="Invalidated"/>, by layer, so a moving playhead redraws the playhead and nothing else.
/// </remarks>
public sealed partial class TimelineViewModel : DocumentViewModel
{
    private readonly ILogger _log = Log.ForContext<TimelineViewModel>();
    private readonly ISession _session;
    private readonly SelectionService _selection;
    private readonly IUiDispatcher _ui;
    private readonly IPreviewEngine? _preview;
    private readonly IDialogService? _dialogs;
    private bool _refreshQueued;
    private bool _selectionQueued;
    private Flicks _playhead;
    private bool _fitted;
    private bool _viewportKnown;
    private double _fittedPixelsPerSecond = double.NaN;
    private readonly Lock _flashGate = new();
    private readonly HashSet<string> _pendingFlash = new(StringComparer.Ordinal);
    private bool _flashQueued;
    private long _flashGeneration;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Opens the timeline of one sequence.</summary>
    public TimelineViewModel(
        ISession session,
        string sequenceId,
        SelectionService selection,
        IUiDispatcher ui,
        IPreviewEngine? preview = null,
        IDialogService? dialogs = null,
        TimelineTools? tools = null)
        : base($"timeline:{sequenceId}", "Timeline")
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrEmpty(sequenceId);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        _selection = selection;
        _ui = ui;
        _preview = preview;
        _dialogs = dialogs;
        Tools = tools ?? new TimelineTools();
        SequenceId = sequenceId;

        _session.ProjectChanged += (_, e) =>
        {
            QueueRefresh();
            if (IsRemote(e.Issuer) && !e.ChangedIds.IsEmpty)
            {
                QueueFlash(e.ChangedIds);
            }
        };
        _selection.Changed += (_, _) => QueueSelection();

        _preview?.PlayheadMoved += (_, e) => _ui.Post(() => OnPlayheadMoved(e));

        Refresh();
        ApplySelection();
    }

    /// <summary>Raised on the UI thread when layers need drawing again.</summary>
    public event EventHandler<TimelineLayers>? Invalidated;

    /// <summary>The sequence this timeline shows.</summary>
    public string SequenceId { get; }

    /// <summary>What is on it.</summary>
    public TimelineContent Content { get; private set; } = TimelineContent.Empty;

    /// <summary>True when the sequence has no clips: the timeline says how to begin.</summary>
    public bool IsEmpty => !Content.Clips.Any();

    /// <summary>Where the view is: zoom, scroll and the track rows.</summary>
    public TimelineGeometry Geometry { get; private set; } = TimelineGeometry.Empty;

    /// <summary>The selected clips and markers of this sequence.</summary>
    public ImmutableHashSet<string> Selected { get; private set; } = ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    /// <summary>
    /// What another client just changed: clips a command from <c>jazz --attach</c>, MCP or the
    /// Command Console touched, outlined for <see cref="FlashDuration"/> and then let go.
    /// </summary>
    public ImmutableHashSet<string> Flashing { get; private set; } = ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    /// <summary>How long a remote change stays outlined: a second.</summary>
    public TimeSpan FlashDuration { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The track headers, in display order, kept by id from one snapshot to the next.</summary>
    public System.Collections.ObjectModel.ObservableCollection<TrackHeaderViewModel> Headers { get; } = [];

    /// <summary>The Quick Trim panel, when this sequence is a Quick Trim; null otherwise.</summary>
    public QuickTrimViewModel? QuickTrim { get; private set; }

    /// <summary>True when the Quick Trim panel shows.</summary>
    public bool IsQuickTrim => QuickTrim is not null;

    /// <summary>True while playback runs, which is when the view follows the playhead.</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>The width of the drawing area, in device independent pixels.</summary>
    public double ViewportWidth { get; private set; } = 800.0;

    /// <summary>The height of the drawing area, ruler and marker lane included.</summary>
    public double ViewportHeight { get; private set; } = 300.0;

    /// <summary>The time at the left edge, in seconds, for the horizontal scroll bar.</summary>
    public double ScrollSeconds
    {
        get => Geometry.Scroll.ToSeconds();
        set => SetGeometry(Geometry with { Scroll = Flicks.FromSeconds(Math.Max(0.0, value)) });
    }

    /// <summary>How many seconds the view shows across.</summary>
    public double ViewportSeconds => ViewportWidth / Geometry.PixelsPerSecond;

    /// <summary>How far the horizontal scroll bar goes: the sequence and a view's width beyond it.</summary>
    public double ScrollMaximum => Math.Max(Content.Sequence.Duration.ToSeconds(), ScrollSeconds);

    /// <summary>How far the tracks are scrolled up.</summary>
    public double VerticalOffset
    {
        get => Geometry.VerticalOffset;
        set => SetGeometry(Geometry with { VerticalOffset = Math.Clamp(value, 0.0, VerticalMaximum) });
    }

    /// <summary>Where the headers are drawn: up by as much as the tracks are scrolled.</summary>
    public double HeadersOffset => -Geometry.VerticalOffset;

    /// <summary>How far the tracks can scroll.</summary>
    public double VerticalMaximum => Math.Max(0.0, Geometry.ContentHeight - TracksViewportHeight);

    /// <summary>How much of the tracks the view shows at once.</summary>
    public double TracksViewportHeight => Math.Max(0.0, ViewportHeight - TimelineGeometry.TracksTop);

    /// <summary>Zoom on a logarithmic scale, for a slider: log10 of pixels per second.</summary>
    public double ZoomLevel
    {
        get => Math.Log10(Geometry.PixelsPerSecond);
        set => SetGeometry(Geometry.ZoomAbout(XOfPlayheadOrCentre(), Math.Pow(10.0, value) / Geometry.PixelsPerSecond));
    }

    /// <summary>The slider's ends.</summary>
    public static double MinZoomLevel => Math.Log10(TimelineGeometry.MinPixelsPerSecond);

    /// <summary>The slider's ends.</summary>
    public static double MaxZoomLevel => Math.Log10(TimelineGeometry.MaxPixelsPerSecond);

    /// <summary>The playhead as of now: the engine's when there is one, else the last seek.</summary>
    public Flicks Playhead => _preview?.Position ?? _playhead;

    /// <summary>The playhead as timecode on this sequence's frame grid.</summary>
    public string Timecode => Core.Time.Timecode.Format(Playhead, Content.Settings.FrameRate);

    /// <summary>Tells the timeline how big the drawing area is.</summary>
    public void SetViewport(double width, double height)
    {
        if (width <= 0 || height <= 0 || (width == ViewportWidth && height == ViewportHeight))
        {
            return;
        }

        ViewportWidth = width;
        ViewportHeight = height;
        _viewportKnown = true;

        // Fitted once there are clips, and fitted again as the width settles for as long as the
        // zoom is still the fitted one: a tab is first measured narrow while it is laid out.
        if (Content.ClipCount > 0 && (!_fitted || Geometry.PixelsPerSecond == _fittedPixelsPerSecond))
        {
            FitView();
            return;
        }

        SetGeometry(Geometry with { VerticalOffset = Math.Clamp(Geometry.VerticalOffset, 0.0, VerticalMaximum) });
    }

    private void FitView()
    {
        _fitted = true;
        SetGeometry(Geometry.Fit(Content.Sequence.Duration, ViewportWidth));
        _fittedPixelsPerSecond = Geometry.PixelsPerSecond;
    }

    /// <summary>Fits the whole sequence in the view.</summary>
    [RelayCommand]
    public void ZoomToFit() => SetGeometry(Geometry.Fit(Content.Sequence.Duration, ViewportWidth));

    /// <summary>Doubles the zoom about the playhead.</summary>
    [RelayCommand]
    public void ZoomIn() => SetGeometry(Geometry.ZoomAbout(XOfPlayheadOrCentre(), 2.0));

    /// <summary>Halves the zoom about the playhead.</summary>
    [RelayCommand]
    public void ZoomOut() => SetGeometry(Geometry.ZoomAbout(XOfPlayheadOrCentre(), 0.5));

    /// <summary>
    /// Keeps the playhead in view while playing, a page at a time, the way a reader turns pages
    /// rather than scrolling a line at a time. Called by the control every frame.
    /// </summary>
    public void Follow(Flicks playhead)
    {
        if (!IsPlaying)
        {
            return;
        }

        double x = Geometry.XOf(playhead);
        if (x > ViewportWidth || x < 0)
        {
            SetGeometry(Geometry with { Scroll = Geometry.Snap(playhead) });
        }
    }

    /// <summary>Replaces the geometry and says so.</summary>
    internal void SetGeometry(TimelineGeometry geometry)
    {
        if (geometry == Geometry)
        {
            return;
        }

        Geometry = geometry;
        OnPropertyChanged(nameof(ScrollSeconds));
        OnPropertyChanged(nameof(ScrollMaximum));
        OnPropertyChanged(nameof(ViewportSeconds));
        OnPropertyChanged(nameof(VerticalOffset));
        OnPropertyChanged(nameof(HeadersOffset));
        OnPropertyChanged(nameof(VerticalMaximum));
        OnPropertyChanged(nameof(TracksViewportHeight));
        OnPropertyChanged(nameof(ZoomLevel));
        Invalidate(TimelineLayers.All);
    }

    /// <summary>Asks the control to draw some layers again.</summary>
    internal void Invalidate(TimelineLayers layers) => Invalidated?.Invoke(this, layers);

    /// <summary>Runs a command and puts any refusal where the person can see it.</summary>
    internal async Task<bool> RunAsync(ICommand command)
    {
        try
        {
            CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(true);
            _ui.Post(() => Status = result.Ok ? string.Empty : result.Error ?? result.Code ?? "That did not work.");
            return result.Ok;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log.Error(exception, "The timeline's {Command} failed", CommandRegistry.NameOf(command));
            _ui.Post(() => Status = exception.Message);
            return false;
        }
    }

    private double XOfPlayheadOrCentre()
    {
        double x = Geometry.XOf(Playhead);
        return x >= 0 && x <= ViewportWidth ? x : ViewportWidth / 2.0;
    }

    private void OnPlayheadMoved(PlayheadMovedEventArgs e)
    {
        _playhead = e.Position;
        IsPlaying = e.State == TransportState.Playing;
        OnPropertyChanged(nameof(Timecode));
    }

    /// <summary>
    /// True for a change someone other than the person at this window made: a remote client
    /// (<c>rpc:*</c>), headless <c>serve</c>, or the Command Console. The window's own edits are
    /// <c>gui</c>, and a session made without an issuer is <c>local</c>.
    /// </summary>
    internal static bool IsRemote(string issuer) => issuer.Length > 0 && issuer is not ("gui" or "local");

    private void QueueFlash(ImmutableArray<string> ids)
    {
        // A remote script of a thousand commands is a thousand events; they gather here and
        // reach the UI thread once.
        lock (_flashGate)
        {
            _pendingFlash.UnionWith(ids);
            if (_flashQueued)
            {
                return;
            }

            _flashQueued = true;
        }

        _ui.Post(() =>
        {
            string[] ids;
            lock (_flashGate)
            {
                ids = [.. _pendingFlash];
                _pendingFlash.Clear();
                _flashQueued = false;
            }

            Flash(ids);
        });
    }

    private void Flash(IReadOnlyCollection<string> ids)
    {
        Flashing = Flashing.Union(ids);
        long generation = ++_flashGeneration;
        Invalidated?.Invoke(this, TimelineLayers.Selection);

        _ = Task.Delay(FlashDuration).ContinueWith(
            _ => _ui.Post(() =>
            {
                // A newer change restarts the second; only the last one lets go.
                if (generation == _flashGeneration)
                {
                    Flashing = Flashing.Clear();
                    Invalidated?.Invoke(this, TimelineLayers.Selection);
                }
            }),
            TaskScheduler.Default);
    }

    private void QueueRefresh()
    {
        // A batch of a thousand commands is a thousand events; the timeline only needs the last.
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        _ui.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private void QueueSelection()
    {
        if (_selectionQueued)
        {
            return;
        }

        _selectionQueued = true;
        _ui.Post(() =>
        {
            _selectionQueued = false;
            ApplySelection();
        });
    }

    private void Refresh()
    {
        Project project = _session.Project;
        Content = TimelineContent.Build(project, SequenceId, Content);
        Title = Content.Sequence.Name.Length > 0 ? Content.Sequence.Name : "Timeline";

        Geometry = Geometry with { Rows = Content.Rows, FrameRate = Content.Settings.FrameRate };
        Geometry = Geometry with { VerticalOffset = Math.Clamp(Geometry.VerticalOffset, 0.0, VerticalMaximum) };

        UpdateHeaders();
        UpdateQuickTrim(project);

        OnPropertyChanged(nameof(Content));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Magnetic));
        OnPropertyChanged(nameof(ScrollMaximum));
        OnPropertyChanged(nameof(VerticalMaximum));
        OnPropertyChanged(nameof(Timecode));

        // The first clips to arrive after the timeline has its size are fitted to the view, as they
        // are when there are clips before it has one: a Quick Trim's tab is sized before its
        // recording lands on it, and opened at the default zoom the recording was a sliver.
        if (!_fitted && _viewportKnown && Content.ClipCount > 0)
        {
            FitView();
        }

        // A clip that went away takes its ghost with it; one that moved is where the model says.
        ClearGesture();
        ApplySelection();
        Invalidate(TimelineLayers.All);
    }

    private void ApplySelection()
    {
        ImmutableHashSet<string> selected = [.. _selection.Ids.Where(id => Content.Clip(id) is not null || Content.Transition(id) is not null || IsMarker(id))];
        selected = selected.WithComparer(StringComparer.Ordinal);

        if (selected.SetEquals(Selected))
        {
            return;
        }

        Selected = selected;
        OnPropertyChanged(nameof(Selected));
        Invalidate(TimelineLayers.Selection | TimelineLayers.Markers);
    }

    private void UpdateQuickTrim(Project project)
    {
        bool wasQuickTrim = QuickTrim is not null;

        if (Content.Sequence.QuickTrim is null)
        {
            QuickTrim = null;
        }
        else
        {
            QuickTrim ??= new QuickTrimViewModel(this, _dialogs);
            QuickTrim.Update(project, Content.Sequence);
        }

        if (wasQuickTrim != QuickTrim is not null)
        {
            OnPropertyChanged(nameof(QuickTrim));
            OnPropertyChanged(nameof(IsQuickTrim));
        }
    }

    private bool IsMarker(string id) =>
        Content.Sequence.Markers.Any(marker => string.Equals(marker.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// A header's height is being dragged: restack the rows as if it were that tall, until the
    /// drag ends and <c>track.set-height</c> makes it so.
    /// </summary>
    internal void PreviewTrackHeight(string trackId, double height)
    {
        var rows = ImmutableArray.CreateBuilder<TrackRow>(Geometry.Rows.Length);
        double top = 0.0;

        foreach (TrackRow row in Geometry.Rows)
        {
            double rowHeight = string.Equals(row.TrackId, trackId, StringComparison.Ordinal) ? height : row.Height;
            rows.Add(row with { Top = top, Height = rowHeight });
            top += rowHeight;
        }

        SetGeometry(Geometry with { Rows = rows.MoveToImmutable() });
    }

    private void UpdateHeaders()
    {
        var existing = Headers.ToDictionary(header => header.TrackId, StringComparer.Ordinal);
        var ordered = new List<TrackHeaderViewModel>(Content.Tracks.Length);
        var counts = new Dictionary<TrackKind, int>();

        // V1 is the lowest picture track and A1 the first sound track, whatever they are named.
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Track track in Content.Sequence.Tracks.OrderBy(track => track.Order))
        {
            int number = counts[track.Kind] = counts.GetValueOrDefault(track.Kind) + 1;
            string prefix = track.Kind switch
            {
                TrackKind.Audio => "A",
                TrackKind.Subtitle => "S",
                TrackKind.Adjustment => "FX",
                _ => "V",
            };

            labels[track.Id] = $"{prefix}{number}";
        }

        HashSet<string> targeted = [.. Core.Editing.ThreePointOps.TargetedTracks(Content.Sequence).Select(track => track.Id)];
        Role[] roles = [.. Role.All(_session.Project)];
        for (int index = 0; index < Content.Tracks.Length; index++)
        {
            TrackView track = Content.Tracks[index];
            TrackHeaderViewModel header = existing.GetValueOrDefault(track.Id) ?? new TrackHeaderViewModel(this, track.Id);
            header.Update(track.Track, labels.GetValueOrDefault(track.Id, string.Empty), Content.Rows[index].Height, targeted.Contains(track.Track.Id), roles);
            ordered.Add(header);
        }

        // Replace in place, so a header that stayed keeps its row in the view and its focus.
        for (int index = 0; index < ordered.Count; index++)
        {
            if (index < Headers.Count)
            {
                if (!ReferenceEquals(Headers[index], ordered[index]))
                {
                    Headers[index] = ordered[index];
                }
            }
            else
            {
                Headers.Add(ordered[index]);
            }
        }

        while (Headers.Count > ordered.Count)
        {
            Headers.RemoveAt(Headers.Count - 1);
        }
    }
}
