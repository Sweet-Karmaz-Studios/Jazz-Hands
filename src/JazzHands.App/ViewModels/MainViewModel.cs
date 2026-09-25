using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.App.ViewModels.Audio;
using JazzHands.App.ViewModels.Export;
using JazzHands.App.ViewModels.Media;
using JazzHands.App.ViewModels.Playback;
using JazzHands.App.ViewModels.Timeline;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Engine.Commands;
using Path = System.IO.Path;

namespace JazzHands.App.ViewModels;

/// <summary>
/// The window: the panels it holds and the title it shows.
/// </summary>
/// <remarks>
/// The panels, the menu (MainViewModel.Shell), the workspaces and the window's place
/// (MainViewModel.Workspaces), the status bar and the project commands.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ISession _session;
    private readonly IFileDialogService? _files;
    private readonly IDialogService? _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly JazzHands.Engine.Settings.SettingsSection<RecentProjects>? _recent;
    private readonly JazzHands.Engine.Settings.SettingsSection<EditorSettings>? _editor;

    [ObservableProperty]
    private string _title = "Jazz Hands";

    /// <summary>Creates the window's viewmodel.</summary>
    public MainViewModel(
        ISession session,
        MediaPanelViewModel media,
        IUiDispatcher ui,
        MetersPanelViewModel? meters = null,
        PreviewPanelViewModel? preview = null,
        TimelineDocuments? timelines = null,
        KeymapService? keys = null,
        IFileDialogService? files = null,
        IDialogService? dialogs = null,
        ExportQueuePanelViewModel? exports = null,
        Inspector.InspectorPanelViewModel? inspector = null,
        Effects.EffectsPanelViewModel? effects = null,
        Grading.ScopesPanelViewModel? scopes = null,
        Grading.ColorPanelViewModel? color = null,
        MixerPanelViewModel? mixer = null,
        Subtitles.SubtitlesPanelViewModel? subtitles = null,
        Remote.CommandConsoleViewModel? console = null,
        JazzHands.Engine.Settings.SettingsSection<RecentProjects>? recent = null,
        History.HistoryPanelViewModel? history = null,
        Logging.LogPanelViewModel? log = null,
        Markers.MarkersPanelViewModel? markers = null,
        JazzHands.Engine.Settings.SettingsSection<EditorSettings>? editor = null,
        StatusBarViewModel? statusBar = null,
        JazzHands.App.Shell.DesktopStatus? desktop = null,
        JazzHands.App.Shell.AppLifetime? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        Media = media;
        Meters = meters;
        Preview = preview;
        Timelines = timelines;
        Keys = keys;
        Exports = exports;
        Inspector = inspector;
        Effects = effects;
        Scopes = scopes;
        Color = color;
        Mixer = mixer;
        Subtitles = subtitles;
        Console = console;
        _files = files;
        _dialogs = dialogs;

        // A key that did nothing says why where the person is looking: the timeline in front.
        keys?.Message += (_, message) => ui.Post(() => Timelines?.ActiveTimeline?.Status = message);

        // So does an export that finished or failed while the person was editing.
        exports?.Notice += (_, message) => Say(message);

        // Tools, snapping and the clipboard are the timeline's, not the engine's.
        keys?.Actions = action => Timelines?.ActiveTimeline?.Invoke(action) == true;
        Panels = [media];

        if (meters is not null)
        {
            Panels.Add(meters);
        }

        if (subtitles is not null)
        {
            Panels.Add(subtitles);
        }

        if (mixer is not null)
        {
            Panels.Add(mixer);
        }

        if (preview is not null)
        {
            Panels.Add(preview);
        }

        if (effects is not null)
        {
            Panels.Add(effects);
        }

        if (inspector is not null)
        {
            Panels.Add(inspector);
        }

        if (exports is not null)
        {
            Panels.Add(exports);
        }

        if (scopes is not null)
        {
            Panels.Add(scopes);
        }

        if (color is not null)
        {
            Panels.Add(color);
        }

        if (console is not null)
        {
            Panels.Add(console);
        }

        foreach (ToolViewModel? panel in new ToolViewModel?[] { history, log, markers })
        {
            if (panel is not null)
            {
                Panels.Add(panel);
            }
        }

        _session.ProjectChanged += (_, _) => ui.Post(UpdateTitle);
        UpdateTitle();

        _ui = ui;
        _recent = recent;
        _editor = editor;
        StatusBar = statusBar;
        Desktop = desktop;
        Lifetime = lifetime;
        if (statusBar is not null)
        {
            Said += (_, message) => statusBar.Notifications.Show(NotificationLevel.Information, message);
        }

        InitializeShell();
    }

    /// <summary>The media panel, which the menu needs by name.</summary>
    public MediaPanelViewModel Media { get; }

    /// <summary>The master meters, when the window has a transport to meter.</summary>
    public MetersPanelViewModel? Meters { get; }

    /// <summary>The program monitor, when the window has a playback engine. The window sends it keys.</summary>
    public PreviewPanelViewModel? Preview { get; }

    /// <summary>The timeline tabs, one per sequence, when the window has them.</summary>
    public TimelineDocuments? Timelines { get; }

    /// <summary>The key bindings, which the window offers every key to before the preview.</summary>
    public KeymapService? Keys { get; }

    /// <summary>The Export Queue panel, when the window has a queue.</summary>
    public ExportQueuePanelViewModel? Exports { get; }

    /// <summary>The inspector, when the editor has one.</summary>
    public Inspector.InspectorPanelViewModel? Inspector { get; }

    /// <summary>The effects browser, when the editor has one.</summary>
    public Effects.EffectsPanelViewModel? Effects { get; }

    /// <summary>The scopes panel, when the host made one.</summary>
    public Grading.ScopesPanelViewModel? Scopes { get; }

    /// <summary>The Subtitles panel, when the host made one.</summary>
    public Subtitles.SubtitlesPanelViewModel? Subtitles { get; }

    /// <summary>The Audio Mixer, when the window has a transport to meter.</summary>
    public MixerPanelViewModel? Mixer { get; }

    /// <summary>The colour wheels and curves panel, when the host made one.</summary>
    public Grading.ColorPanelViewModel? Color { get; }

    /// <summary>The status bar and the notifications, when the host has them.</summary>
    public StatusBarViewModel? StatusBar { get; }

    /// <summary>What the taskbar button and the notification area show, when the host has them.</summary>
    public DesktopStatus? Desktop { get; }

    /// <summary>Hiding, showing and quitting, when the host keeps the editor running without its window.</summary>
    public AppLifetime? Lifetime { get; }

    /// <summary>Play or pause, from the button on the taskbar thumbnail.</summary>
    [RelayCommand]
    private Task ThumbPlayPauseAsync() => _session.ExecuteAsync(new TogglePlaybackCommand());

    /// <summary>The previous edit, from the taskbar thumbnail.</summary>
    [RelayCommand]
    private Task ThumbPreviousEditAsync() => _session.ExecuteAsync(new GoToCommand(GoToTarget.PrevEdit));

    /// <summary>The next edit, from the taskbar thumbnail.</summary>
    [RelayCommand]
    private Task ThumbNextEditAsync() => _session.ExecuteAsync(new GoToCommand(GoToTarget.NextEdit));

    /// <summary>The Command Console, when the host has a control server.</summary>
    public Remote.CommandConsoleViewModel? Console { get; }

    /// <summary>
    /// Raised with the content ids of panels to bring to the front: a workspace, until Phase 27's
    /// layout service saves and restores whole layouts. The window, which owns the docking
    /// manager, shows them.
    /// </summary>
    public event EventHandler<IReadOnlyList<string>>? PanelsRequested;

    /// <summary>Every dockable panel, in the order they were registered.</summary>
    public ObservableCollection<ToolViewModel> Panels { get; }

    /// <summary>The colour workspace: the Color panel and the scopes in front, beside the preview.</summary>
    [RelayCommand]
    private void ShowColorWorkspace() => PanelsRequested?.Invoke(this, [Grading.ColorPanelViewModel.PanelId, Grading.ScopesPanelViewModel.PanelId]);

    /// <summary>The audio workspace: the Mixer in front, its strips and the master beside the preview.</summary>
    [RelayCommand]
    private void ShowAudioWorkspace() => PanelsRequested?.Invoke(this, [MixerPanelViewModel.PanelId]);

    /// <summary>Brings the Command Console to the front.</summary>
    [RelayCommand]
    private void ShowConsole() => PanelsRequested?.Invoke(this, [Remote.CommandConsoleViewModel.PanelId]);

    [RelayCommand]
    private Task ExitAsync() => Lifetime?.QuitFromPersonAsync() ?? ShutdownNow();

    private static Task ShutdownNow()
    {
        Application.Current?.Shutdown();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Picks a recording, brings it into the project and starts a Quick Trim of it: <c>media.add</c>
    /// then <c>trim.start</c>, the two commands <c>jazz trim</c> sends.
    /// </summary>
    [RelayCommand]
    private async Task QuickTrimFileAsync()
    {
        if (_files?.OpenMovie() is { } chosen)
        {
            await QuickTrimAsync(chosen).ConfigureAwait(true);
        }
    }

    /// <summary>Brings a recording in and starts a Quick Trim of it: Explorer's Quick Trim with Jazz Hands.</summary>
    public async Task QuickTrimAsync(string file)
    {
        string path = Path.GetFullPath(file);
        CommandResult added = await _session.ExecuteAsync(new AddMediaCommand([path])).ConfigureAwait(true);
        if (!added.Ok && added.Code != "already-imported")
        {
            Say(added.Error ?? "The recording could not be brought in.");
            return;
        }

        Project project = _session.Project;
        MediaItem? media = added.Ok
            ? added.ChangedIds.Select(project.MediaItem).FirstOrDefault(item => item is not null)
            : project.Media.FirstOrDefault(item => string.Equals(FullPathOf(item), path, StringComparison.OrdinalIgnoreCase));

        if (media is null)
        {
            Say("The recording is in the project already under another name; start the Quick Trim from the media panel.");
            return;
        }

        CommandResult started = await _session.ExecuteAsync(new StartTrimCommand(media.Id)).ConfigureAwait(true);
        if (!started.Ok)
        {
            Say(started.Error ?? "The Quick Trim could not start.");
        }
    }

    /// <summary>Opens the export dialog for the sequence in front.</summary>
    [RelayCommand]
    private Task ExportAsync() => _dialogs?.ShowExportAsync(_session.Project.ActiveSequenceId) ?? Task.CompletedTask;

    private string FullPathOf(MediaItem item) =>
        Path.IsPathRooted(item.RelativePath) || _session.ProjectPath.Length == 0
            ? Path.GetFullPath(item.RelativePath)
            : ProjectPaths.Resolve(_session.ProjectPath, item.RelativePath);

    private void Say(string message)
    {
        if (Timelines?.ActiveTimeline is { } timeline)
        {
            timeline.Status = message;
        }

        Said?.Invoke(this, message);
    }

    private void UpdateTitle()
    {
        string name = _session.ProjectPath.Length > 0
            ? Path.GetFileNameWithoutExtension(_session.ProjectPath)
            : _session.Project.Name;

        Title = name.Length > 0 ? $"{name} - Jazz Hands" : "Jazz Hands";
    }
}
