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
using Serilog;
using ICommand = JazzHands.Core.Commands.ICommand;

namespace JazzHands.App.ViewModels.Playback;

/// <summary>How big the picture is drawn in the panel.</summary>
public enum PreviewZoom
{
    /// <summary>As large as fits, letterboxed.</summary>
    Fit,

    /// <summary>One sequence pixel to one screen pixel.</summary>
    Actual,

    /// <summary>Two screen pixels to each sequence pixel.</summary>
    Double,
}

/// <summary>
/// The program monitor: the picture, the transport bar, the timecode, and the keys that drive
/// playback.
/// </summary>
/// <remarks>
/// Every button and every key ends in a command sent through the session, the same command the
/// CLI and MCP send, so there is nothing the panel can do that a script cannot. What comes back
/// is the engine's <see cref="IPreviewEngine.PlayheadMoved"/> event, at most thirty times a
/// second while playing, which is what the timecode and the play button follow.
///
/// The picture itself never passes through here: the view hands the engine a presenter and the
/// engine draws into it on its own thread. The viewmodel only knows what the view needs to lay
/// out overlays: the sequence's size and the zoom.
/// </remarks>
public sealed partial class PreviewPanelViewModel : ToolViewModel
{
    /// <summary>The id the layout knows the panel by.</summary>
    public const string PanelId = "preview";

    private readonly ILogger _log = Log.ForContext<PreviewPanelViewModel>();
    private readonly ISession _session;
    private readonly IPreviewEngine _engine;
    private readonly IUiDispatcher _ui;
    private readonly IFullScreenPreview? _fullScreen;
    private readonly IDisplaySettings? _displaySettings;

    /// <summary>True while the window is hidden: the view lets go of its surfaces until it shows again.</summary>
    [ObservableProperty]
    private bool _isSuspended;

    /// <summary>What the monitor expects; the presenters convert the delivered signal for it.</summary>
    [ObservableProperty]
    private DisplayTransfer _display;
    private readonly PointPicker? _picker;
    private readonly JklShuttle _shuttle = new();
    private bool _applyingState;

    [ObservableProperty]
    private string _timecode = "00:00:00:00";

    [ObservableProperty]
    private string _duration = "00:00:00:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayGlyph), nameof(PlayLabel))]
    private bool _isPlaying;

    [ObservableProperty]
    private string _rateText = string.Empty;

    [ObservableProperty]
    private bool _loop;

    [ObservableProperty]
    private PreviewQuality _quality = PreviewQuality.Auto;

    /// <summary>True when proxies play in place of their sources; see proxy.set-enabled.</summary>
    [ObservableProperty]
    private bool _useProxies;

    [ObservableProperty]
    private PreviewZoom _zoom = PreviewZoom.Fit;

    [ObservableProperty]
    private bool _showSafeAreas;

    [ObservableProperty]
    private bool _showGrid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDroppedFrames))]
    private long _droppedFrames;

    [ObservableProperty]
    private int _sequenceWidth = ProjectSettings.Default.Width;

    [ObservableProperty]
    private int _sequenceHeight = ProjectSettings.Default.Height;

    [ObservableProperty]
    private string _format = string.Empty;

    [ObservableProperty]
    private string _inOut = string.Empty;

    [ObservableProperty]
    private string _renderStats = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullScreenGlyph))]
    private bool _isFullScreen;

    /// <summary>The selected title's frame and handles on the picture; null where the panel has no selection to follow.</summary>
    public TitleHandlesViewModel? Titles { get; }

    /// <summary>Creates the panel.</summary>
    public PreviewPanelViewModel(ISession session, IPreviewEngine engine, IUiDispatcher ui, IFullScreenPreview? fullScreen = null, PointPicker? picker = null, IDisplaySettings? display = null, TitleHandlesViewModel? titles = null)
        : base(PanelId, "Preview")
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        _engine = engine;
        _ui = ui;
        _fullScreen = fullScreen;
        _picker = picker;
        _displaySettings = display;
        Titles = titles;
        _display = display?.Transfer ?? DisplayTransfer.Srgb;

        // The inspector's points and picks show on the picture.
        _picker?.PropertyChanged += (_, _) => _ui.Post(() =>
        {
            OnPropertyChanged(nameof(Markers));
            OnPropertyChanged(nameof(IsPicking));
            OnPropertyChanged(nameof(PickHint));
        });

        _engine.PlayheadMoved += (_, _) => _ui.Post(Refresh);
        _session.ProjectChanged += (_, _) => _ui.Post(Refresh);

        _fullScreen?.IsOpenChanged += (_, _) => _ui.Post(() => IsFullScreen = _fullScreen.IsOpen);

        Refresh();
    }

    /// <summary>The inspector's point parameters, for the overlay.</summary>
    public IReadOnlyList<PreviewMarker> Markers => _picker?.Markers ?? [];

    /// <summary>True while a click on the picture sets a point.</summary>
    public bool IsPicking => _picker?.IsPicking == true;

    /// <summary>What a click will set, for the hint over the picture.</summary>
    public string PickHint => IsPicking ? $"Click the picture to set {_picker!.Picking}. Right-click to cancel." : string.Empty;

    /// <summary>A click on the picture while picking: hands the point on, in sequence pixels from the centre.</summary>
    public bool PickAt(System.Numerics.Vector2 fromCentre) => _picker?.Pick(fromCentre) == true;

    /// <summary>Gives up a pick.</summary>
    public void CancelPick() => _picker?.Cancel();

    /// <summary>The engine, for the view to attach its presenter to.</summary>
    public IPreviewEngine Engine => _engine;

    /// <summary>The quality choices, in menu order.</summary>
    public IReadOnlyList<PreviewQuality> Qualities { get; } =
        [PreviewQuality.Auto, PreviewQuality.Full, PreviewQuality.Half, PreviewQuality.Quarter];

    /// <summary>The zoom choices, in menu order.</summary>
    public IReadOnlyList<PreviewZoom> Zooms { get; } = [PreviewZoom.Fit, PreviewZoom.Actual, PreviewZoom.Double];

    /// <summary>True once playback has dropped a frame, which is when the count is worth showing.</summary>
    public bool HasDroppedFrames => DroppedFrames > 0;

    /// <summary>The play button's glyph: pause while playing, play otherwise.</summary>
    public string PlayGlyph => IsPlaying ? Glyphs.Pause : Glyphs.Play;

    /// <summary>The play button's name, for tooltips and screen readers.</summary>
    public string PlayLabel => IsPlaying ? "Pause (Space)" : "Play (Space)";

    /// <summary>The full screen button's glyph: back to a window while open, full screen otherwise.</summary>
    public string FullScreenGlyph => IsFullScreen ? Glyphs.BackToWindow : Glyphs.FullScreen;

    /// <summary>
    /// A key went down anywhere in the window that is not typing. Returns true when it was a
    /// playback key, so the window marks it handled.
    /// </summary>
    public bool KeyDown(Key key, ModifierKeys modifiers, bool isRepeat)
    {
        bool shift = modifiers == ModifierKeys.Shift;
        bool none = modifiers == ModifierKeys.None;

        if (key == Key.F11 && none)
        {
            if (!isRepeat)
            {
                _fullScreen?.Toggle();
            }

            return true;
        }

        ICommand? command = (key, none, shift) switch
        {
            (Key.Space, true, _) when !isRepeat => new TogglePlaybackCommand(),
            (Key.J, true, _) when !isRepeat || _shuttle.IsHoldingK => _shuttle.Press(ShuttleKey.J),
            (Key.L, true, _) when !isRepeat || _shuttle.IsHoldingK => _shuttle.Press(ShuttleKey.L),
            (Key.K, true, _) when !isRepeat => _shuttle.Press(ShuttleKey.K),
            (Key.Left, true, _) => new StepCommand(-1),
            (Key.Right, true, _) => new StepCommand(1),
            (Key.Left, _, true) => new StepCommand(-10),
            (Key.Right, _, true) => new StepCommand(10),
            (Key.Up, true, _) => new GoToCommand(GoToTarget.PrevEdit),
            (Key.Down, true, _) => new GoToCommand(GoToTarget.NextEdit),
            (Key.Home, true, _) => new GoToCommand(GoToTarget.Start),
            (Key.End, true, _) => new GoToCommand(GoToTarget.End),
            (Key.I, true, _) when !isRepeat => new SetInPointCommand(),
            (Key.O, true, _) when !isRepeat => new SetOutPointCommand(),
            (Key.I, _, true) when !isRepeat => new GoToCommand(GoToTarget.In),
            (Key.O, _, true) when !isRepeat => new GoToCommand(GoToTarget.Out),
            (Key.X, _, _) when modifiers == (ModifierKeys.Control | ModifierKeys.Shift) => new ClearInOutCommand(),
            (Key.L, _, _) when modifiers == ModifierKeys.Control && !isRepeat => new SetLoopCommand(),
            _ => null,
        };

        if (command is null)
        {
            // A held key's repeats are still ours even when they do nothing, or they would fall
            // through to whatever has focus.
            return isRepeat && none && key is Key.Space or Key.J or Key.K or Key.L or Key.I or Key.O;
        }

        Send(command);
        return true;
    }

    /// <summary>A key came up. Only K's release matters.</summary>
    public bool KeyUp(Key key)
    {
        if (key != Key.K)
        {
            return false;
        }

        _shuttle.Release(ShuttleKey.K);
        return true;
    }

    /// <summary>Reads the engine and the project into the properties the view binds.</summary>
    public void Refresh()
    {
        PlaybackStateInfo state = _engine.Describe();
        Project project = _session.Project;
        Sequence? sequence = project.ActiveSequence;

        _applyingState = true;
        try
        {
            bool playing = state.State == "playing";
            IsPlaying = playing;
            Timecode = state.Timecode;
            RateText = playing && state.Rate != 1.0 ? $"{state.Rate:0.##}x" : string.Empty;
            Loop = state.Loop;
            Quality = state.Quality;
            UseProxies = state.ProxiesEnabled;
            DroppedFrames = _engine.DroppedFrames;
            RenderStats = DescribeRender(state.Render);

            if (sequence is not null)
            {
                ProjectSettings settings = project.SettingsFor(sequence);
                SequenceWidth = settings.Width;
                SequenceHeight = settings.Height;
                Duration = Core.Time.Timecode.Format(sequence.Duration, settings.FrameRate);
                Format = $"{settings.Width}x{settings.Height}  {FormatRate(settings.FrameRate)} fps";
                InOut = sequence.InOut is { } range
                    ? $"In {Core.Time.Timecode.Format(range.Start, settings.FrameRate)}  Out {Core.Time.Timecode.Format(range.End - settings.FrameDuration, settings.FrameRate)}"
                    : string.Empty;
            }

            _shuttle.Sync(playing, state.Rate);
        }
        finally
        {
            _applyingState = false;
        }
    }

    /// <summary>Frame rates as people say them: 30, 29.97, 23.976.</summary>
    public static string FormatRate(Rational rate) =>
        rate.Den == 1
            ? rate.Num.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : ((double)rate.Num / rate.Den).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The render pools in a line, for the tooltip on the format readout. Created counts that stay
    /// put while playing are the sign nothing is being allocated per frame.
    /// </summary>
    public static string DescribeRender(RenderStatsInfo? stats) =>
        stats is null
            ? string.Empty
            : string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Render targets: {stats.TargetsCreated} created, {stats.TargetsOutstanding} in use, {stats.TargetsRented:N0} rented. "
                + $"Frame textures: {stats.FrameTexturesCreated} created. Layers cached: {stats.LayersCached}.");

    partial void OnQualityChanged(PreviewQuality value)
    {
        if (!_applyingState)
        {
            Send(new SetQualityCommand(value));
        }
    }

    partial void OnUseProxiesChanged(bool value)
    {
        if (!_applyingState)
        {
            Send(new SetProxiesEnabledCommand(value));
        }
    }

    partial void OnLoopChanged(bool value)
    {
        if (!_applyingState)
        {
            Send(new SetLoopCommand(value));
        }
    }

    partial void OnZoomChanged(PreviewZoom value) => _engine.Refresh();

    [RelayCommand]
    private void TogglePlay() => Send(new TogglePlaybackCommand());

    [RelayCommand]
    private void Stop() => Send(new StopPlaybackCommand());

    [RelayCommand]
    private void StepBack() => Send(new StepCommand(-1));

    [RelayCommand]
    private void StepForward() => Send(new StepCommand(1));

    [RelayCommand]
    private void GoToStart() => Send(new GoToCommand(GoToTarget.Start));

    [RelayCommand]
    private void GoToEnd() => Send(new GoToCommand(GoToTarget.End));

    [RelayCommand]
    private void SetIn() => Send(new SetInPointCommand());

    [RelayCommand]
    private void SetOut() => Send(new SetOutPointCommand());

    [RelayCommand]
    private void ClearInOut() => Send(new ClearInOutCommand());

    [RelayCommand]
    private void ToggleFullScreen() => _fullScreen?.Toggle();

    /// <summary>Segoe Fluent Icons code points the panel switches between.</summary>
    private static class Glyphs
    {
        public static readonly string Play = char.ConvertFromUtf32(0xE768);
        public static readonly string Pause = char.ConvertFromUtf32(0xE769);
        public static readonly string FullScreen = char.ConvertFromUtf32(0xE740);
        public static readonly string BackToWindow = char.ConvertFromUtf32(0xE73F);
    }

    /// <summary>Sends a command and does not wait: the panel follows the engine's events, not the result.</summary>
    private void Send(ICommand command) => _ = SendAsync(command);

    private async Task SendAsync(ICommand command)
    {
        try
        {
            CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(false);

            // Going to the next edit when there is none is not worth a message; anything else
            // that fails is somebody's bug and goes in the log.
            if (!result.Ok && result.Code is not ("no-target" or "no-sequence"))
            {
                _log.Warning(
                    "{Command} was refused: {Code}: {Error}",
                    CommandRegistry.NameOf(command),
                    result.Code,
                    result.Error);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log.Error(exception, "{Command} failed", CommandRegistry.NameOf(command));
        }
    }

    partial void OnDisplayChanged(DisplayTransfer value)
    {
        _displaySettings?.Transfer = value;

        // The frame on screen is presented again, converted the new way.
        _engine.Refresh();
    }

    /// <summary>Chooses what the monitor expects, from the Window menu.</summary>
    [RelayCommand]
    private void SetDisplay(DisplayTransfer transfer) => Display = transfer;
}
