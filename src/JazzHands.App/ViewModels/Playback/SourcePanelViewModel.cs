using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Playback;
using JazzHands.Render.Color;
using ICommand = JazzHands.Core.Commands.ICommand;

namespace JazzHands.App.ViewModels.Playback;

/// <summary>
/// The source monitor panel (Phase 38): one file with its own transport, scrub bar, timecode and
/// in and out marks, and the insert and overwrite that put the marked stretch in the sequence.
/// </summary>
/// <remarks>
/// Everything it does is a command: <c>source.*</c> for the viewer's state (which is the
/// session's <see cref="SourceMonitor"/>, so Claude Code sees and sets the same marks), and
/// <c>clip.insert-from-source</c> or <c>clip.overwrite-from-source</c> with every value spelled
/// out, so the edit in the history is the same edit on replay. It asks
/// <c>clip.plan-from-source</c> first, for what a four-point edit decided.
/// </remarks>
public sealed partial class SourcePanelViewModel : ToolViewModel, IQuietWhileHidden, IDisposable
{
    /// <summary>The panel's content id.</summary>
    public const string PanelId = "source";

    private readonly ISession _session;
    private readonly SourceMonitor _monitor;
    private readonly ISourceScreen? _screen;
    private readonly IUiDispatcher _ui;
    private readonly Func<Flicks> _programPlayhead;
    private int _positionQueued;
    private string? _shown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHint))]
    private bool _hasSource;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _timecode = "00:00:00:00";

    [ObservableProperty]
    private string _inText = "--:--:--:--";

    [ObservableProperty]
    private string _outText = "--:--:--:--";

    [ObservableProperty]
    private string _markedText = string.Empty;

    [ObservableProperty]
    private double _fraction;

    [ObservableProperty]
    private double? _inFraction;

    [ObservableProperty]
    private double? _outFraction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayGlyph), nameof(PlayLabel))]
    private bool _isPlaying;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isSuspended;

    [ObservableProperty]
    private DisplayTransfer _display;

    /// <summary>A panel over the session's source monitor.</summary>
    /// <param name="session">The session.</param>
    /// <param name="monitor">The session's source monitor.</param>
    /// <param name="screen">The player the view draws, or null (no device, tests).</param>
    /// <param name="ui">The UI thread.</param>
    /// <param name="programPlayhead">Where the program's playhead is, for an edit with no sequence in point.</param>
    /// <param name="display">The display preference.</param>
    public SourcePanelViewModel(ISession session, SourceMonitor monitor, ISourceScreen? screen, IUiDispatcher ui, Func<Flicks> programPlayhead, IDisplaySettings? display = null)
        : base(PanelId, "Source")
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(programPlayhead);
        _session = session;
        _monitor = monitor;
        _screen = screen;
        _ui = ui;
        _programPlayhead = programPlayhead;
        _display = display?.Transfer ?? DisplayTransfer.Srgb;

        _monitor.Changed += OnMonitorChanged;
        _session.ProjectChanged += OnProjectChanged;
        if (_screen is not null)
        {
            _screen.PlayheadMoved += OnPlayheadMoved;
            _screen.ItemGone += OnItemGone;
        }

        Refresh();
    }

    /// <summary>True while nothing is open, to say how to open something.</summary>
    public bool ShowHint => !HasSource;

    /// <summary>The play button's glyph.</summary>
    public string PlayGlyph => char.ConvertFromUtf32(IsPlaying ? 0xE769 : 0xE768);

    /// <summary>The play button's name.</summary>
    public string PlayLabel => IsPlaying ? "Pause (Space)" : "Play (Space)";

    /// <summary>Raised on the UI thread when a different item is opened, from anywhere.</summary>
    public event EventHandler? Opened;

    /// <summary>The player, for the view to draw into.</summary>
    public ISourceScreen? Screen => _screen;

    /// <summary>The item open, or null.</summary>
    public MediaItem? Item => _monitor.MediaId is { } id ? _session.Project.MediaItem(id) : null;

    /// <summary>The stretch an edit or a drag takes: the marks, or the item's own range where one is missing.</summary>
    public (Flicks In, Flicks Out) MarkedRange => (_monitor.In ?? Item?.DefaultIn ?? Flicks.Zero, _monitor.Out ?? Item?.DefaultOut ?? Flicks.Zero);

    /// <summary>Where the project is, for a dragged file's path.</summary>
    public string ProjectPath => _session.ProjectPath;

    private Rational Rate => Item?.Info?.VideoStreams.FirstOrDefault()?.FrameRate is { IsZero: false } rate ? rate : _session.Project.Settings.FrameRate;

    /// <inheritdoc />
    public void SetQuiet(bool quiet)
    {
        IsSuspended = quiet;
        _screen?.Suspended = quiet;
    }

    /// <summary>Reads the monitor and the item again.</summary>
    public void Refresh()
    {
        MediaItem? item = Item;
        HasSource = item is not null;
        Name = item?.Name ?? string.Empty;
        IsPlaying = _monitor.IsPlaying;
        Rational rate = Rate;
        InText = _monitor.In is { } markIn ? Core.Time.Timecode.Format(markIn, rate) : "--:--:--:--";
        OutText = _monitor.Out is { } markOut ? Core.Time.Timecode.Format(markOut - Flicks.FromFrames(1, rate), rate) : "--:--:--:--";
        Flicks from = _monitor.In ?? item?.DefaultIn ?? Flicks.Zero;
        Flicks to = _monitor.Out ?? item?.DefaultOut ?? Flicks.Zero;
        MarkedText = item is null || to <= from ? string.Empty : $"{Core.Time.Timecode.Format(to - from, rate)} marked";
        double length = Math.Max(1, item?.Duration.Value ?? 1);
        InFraction = _monitor.In is { } a ? a.Value / length : null;
        OutFraction = _monitor.Out is { } b ? b.Value / length : null;
        LoadMarks(item, length);
        RefreshPosition();
    }

    private void RefreshPosition()
    {
        Interlocked.Exchange(ref _positionQueued, 0);
        Flicks position = _monitor.Position;
        Timecode = Core.Time.Timecode.Format(position, Rate);
        double length = Item?.Duration.Value ?? 0;
        Fraction = length > 0 ? position.Value / length : 0;
        IsPlaying = _monitor.IsPlaying;
    }

    private void OnMonitorChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        Refresh();
        if (_monitor.MediaId is { } id && !string.Equals(id, _shown, StringComparison.Ordinal))
        {
            Opened?.Invoke(this, EventArgs.Empty);
        }

        _shown = _monitor.MediaId;
    });

    private void OnProjectChanged(object? sender, ProjectChangedEventArgs e) => _ui.Post(Refresh);

    private void OnPlayheadMoved(object? sender, PlayheadMovedEventArgs e)
    {
        // Folded: one update on the UI thread however many frames went by meanwhile.
        if (Interlocked.Exchange(ref _positionQueued, 1) == 0)
        {
            _ui.Post(RefreshPosition);
        }
    }

    private void OnItemGone(object? sender, EventArgs e) => _ui.Post(() =>
    {
        _monitor.Close();
        Status = "The file in the source monitor left the project.";
    });

    /// <summary>Opens a media item, as a double-click in the Media panel does.</summary>
    public Task OpenAsync(string mediaId, Flicks? at = null) => SendAsync(new OpenSourceCommand(mediaId, at));

    /// <summary>Moves the playhead to a fraction of the file, from the scrub bar.</summary>
    public Task ScrubAsync(double fraction) => Item is { } item
        ? SendAsync(new SeekSourceCommand(new Flicks((long)(Math.Clamp(fraction, 0, 1) * item.Duration.Value)).SnapToFrame(Rate)))
        : Task.CompletedTask;

    [RelayCommand]
    private Task PlayPauseAsync() => HasSource ? SendAsync(new PlaySourceCommand()) : Task.CompletedTask;

    [RelayCommand]
    private Task MarkInAsync() => HasSource ? SendAsync(new SetSourceInCommand()) : Task.CompletedTask;

    [RelayCommand]
    private Task MarkOutAsync() => HasSource ? SendAsync(new SetSourceOutCommand()) : Task.CompletedTask;

    [RelayCommand]
    private Task ClearMarksAsync() => HasSource ? SendAsync(new ClearSourceInOutCommand()) : Task.CompletedTask;

    [RelayCommand]
    private Task GoToInAsync() => _monitor.In is { } markIn ? SendAsync(new SeekSourceCommand(markIn)) : Task.CompletedTask;

    [RelayCommand]
    private Task GoToOutAsync() => _monitor.Out is { } markOut ? SendAsync(new SeekSourceCommand(markOut - Flicks.FromFrames(1, Rate))) : Task.CompletedTask;

    [RelayCommand]
    private Task StepBackAsync() => StepAsync(-1);

    [RelayCommand]
    private Task StepForwardAsync() => StepAsync(1);

    /// <summary>Steps a number of frames.</summary>
    public Task StepAsync(int frames)
    {
        if (Item is not { } item)
        {
            return Task.CompletedTask;
        }

        Flicks frame = Flicks.FromFrames(1, Rate);
        Flicks to = Flicks.Clamp(_monitor.Position.SnapToFrame(Rate) + (frame * frames), Flicks.Zero, item.Duration - frame);
        return SendAsync(new SeekSourceCommand(to));
    }

    /// <summary>Puts the marked stretch in the sequence, pushing the rest along (the comma key, F9).</summary>
    [RelayCommand]
    public Task InsertAsync() => EditAsync(insert: true);

    /// <summary>Puts the marked stretch over the sequence (the full stop key, F10).</summary>
    [RelayCommand]
    public Task OverwriteAsync() => EditAsync(insert: false);

    private async Task EditAsync(bool insert)
    {
        if (Item is not { } item)
        {
            Status = "Open a media item first: double-click it in the Media panel.";
            return;
        }

        Flicks playhead = _programPlayhead();
        SourceEditPlan plan;
        try
        {
            plan = _session.Query(new PlanFromSourceQuery(item.Id, _monitor.In, _monitor.Out, playhead));
        }
        catch (CommandException error)
        {
            Status = error.Message;
            return;
        }

        ICommand edit = insert
            ? new InsertFromSourceCommand(item.Id, plan.SourceIn, plan.SourceIn + plan.Duration, plan.At)
            : new OverwriteFromSourceCommand(item.Id, plan.SourceIn, plan.SourceIn + plan.Duration, plan.At);
        CommandResult result = await _session.ExecuteAsync(edit).ConfigureAwait(true);
        if (!result.Ok)
        {
            Status = result.Error ?? "The edit did not go in.";
            return;
        }

        // As in every editor, the playhead moves to the end of what went in, ready for the next.
        _ = await _session.ExecuteAsync(new SeekCommand(plan.At + plan.Duration)).ConfigureAwait(true);
        Status = plan.Note ?? string.Create(CultureInfo.InvariantCulture, $"{(insert ? "Inserted" : "Overwrote")} {Core.Time.Timecode.FormatClock(plan.Duration)} of {item.Name}.");
    }

    /// <summary>Finds the source frame in the sequence and puts the program playhead there (Shift+F).</summary>
    [RelayCommand]
    public async Task MatchInSequenceAsync()
    {
        if (Item is not { } item)
        {
            return;
        }

        try
        {
            SourceFrameInfo found = _session.Query(new FindSourceFrameQuery(item.Id, _monitor.Position));
            await _session.ExecuteAsync(new SeekCommand(found.Time)).ConfigureAwait(true);
            Status = $"Found at {Core.Time.Timecode.FormatClock(found.Time)} in the sequence.";
        }
        catch (CommandException error)
        {
            Status = error.Message;
        }
    }

    /// <summary>The source monitor's keys, while it has the focus. True when the key was used.</summary>
    public bool KeyDown(Key key, ModifierKeys modifiers)
    {
        Task? work = (key, modifiers) switch
        {
            (Key.Space, ModifierKeys.None) => PlayPauseAsync(),
            (Key.K, ModifierKeys.None) => IsPlaying ? PlayPauseAsync() : Task.CompletedTask,
            (Key.L, ModifierKeys.None) => IsPlaying ? Task.CompletedTask : PlayPauseAsync(),
            (Key.I, ModifierKeys.None) => MarkInAsync(),
            (Key.O, ModifierKeys.None) => MarkOutAsync(),
            (Key.I, ModifierKeys.Shift) => GoToInAsync(),
            (Key.O, ModifierKeys.Shift) => GoToOutAsync(),
            (Key.X, ModifierKeys.Control | ModifierKeys.Shift) => ClearMarksAsync(),
            (Key.Left, ModifierKeys.None) => StepAsync(-1),
            (Key.Right, ModifierKeys.None) => StepAsync(1),
            (Key.Left, ModifierKeys.Shift) => StepAsync(-10),
            (Key.Right, ModifierKeys.Shift) => StepAsync(10),
            (Key.Home, ModifierKeys.None) => HasSource ? SendAsync(new SeekSourceCommand(Flicks.Zero)) : Task.CompletedTask,
            (Key.OemComma, ModifierKeys.None) => InsertAsync(),
            (Key.OemPeriod, ModifierKeys.None) => OverwriteAsync(),
            (Key.F, ModifierKeys.Shift) => MatchInSequenceAsync(),
            (Key.Up, ModifierKeys.Shift) => ToMarkAsync(forward: false),
            (Key.Down, ModifierKeys.Shift) => ToMarkAsync(forward: true),
            _ => null,
        };

        return work is not null;
    }

    /// <summary>The item's own markers (scene cuts and the like) along the scrub bar, at their place in it.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SourceMark> Marks { get; } = [];

    /// <summary>Goes to the item's next or previous marker from the source playhead (Shift+Down, Shift+Up).</summary>
    public Task ToMarkAsync(bool forward)
    {
        if (Item is not { } item)
        {
            return Task.CompletedTask;
        }

        Flicks here = _monitor.Position.SnapToFrame(Rate);
        Flicks[] times = [.. item.Markers.Select(marker => marker.Time.SnapToFrame(Rate)).Order()];
        Flicks? to = forward ? times.Cast<Flicks?>().FirstOrDefault(time => time > here) : times.Cast<Flicks?>().LastOrDefault(time => time < here);
        return to is { } time ? SendAsync(new SeekSourceCommand(time)) : Task.CompletedTask;
    }

    private void LoadMarks(MediaItem? item, double length)
    {
        SourceMark[] marks = item is null
            ? []
            : [.. item.Markers.Select(marker => new SourceMark(marker.Time.Value / length, marker.Color, marker.Name))];
        if (!marks.SequenceEqual(Marks))
        {
            Marks.Clear();
            foreach (SourceMark mark in marks)
            {
                Marks.Add(mark);
            }
        }
    }

    private async Task SendAsync(ICommand command)
    {
        CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(true);
        Status = result.Ok ? string.Empty : result.Error ?? string.Empty;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _monitor.Changed -= OnMonitorChanged;
        _session.ProjectChanged -= OnProjectChanged;
        if (_screen is not null)
        {
            _screen.PlayheadMoved -= OnPlayheadMoved;
            _screen.ItemGone -= OnItemGone;
        }
    }
}

/// <summary>One of a media item's markers on the source monitor's scrub bar.</summary>
/// <param name="Fraction">Where it is, as a fraction of the file.</param>
/// <param name="Color">Its colour.</param>
/// <param name="Name">What it is called.</param>
public sealed record SourceMark(double Fraction, string Color, string Name);
