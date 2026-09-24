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
/// Phase 27 gives this the layout service, the workspaces and the menu built from the command
/// registry. Right now it owns one panel and the two things the window itself needs to know.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ISession _session;
    private readonly IFileDialogService? _files;
    private readonly IDialogService? _dialogs;

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
        ExportQueuePanelViewModel? exports = null)
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
        _files = files;
        _dialogs = dialogs;

        // A key that did nothing says why where the person is looking: the timeline in front.
        keys?.Message += (_, message) => ui.Post(() => Timelines?.ActiveTimeline?.Status = message);

        // Tools, snapping and the clipboard are the timeline's, not the engine's.
        keys?.Actions = action => Timelines?.ActiveTimeline?.Invoke(action) == true;
        Panels = [media];

        if (meters is not null)
        {
            Panels.Add(meters);
        }

        if (preview is not null)
        {
            Panels.Add(preview);
        }

        if (exports is not null)
        {
            Panels.Add(exports);
        }

        _session.ProjectChanged += (_, _) => ui.Post(UpdateTitle);
        UpdateTitle();
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

    /// <summary>Every dockable panel, in the order they were registered.</summary>
    public ObservableCollection<ToolViewModel> Panels { get; }

    [RelayCommand]
    private static void Exit() => Application.Current?.Shutdown();

    /// <summary>
    /// Picks a recording, brings it into the project and starts a Quick Trim of it: <c>media.add</c>
    /// then <c>trim.start</c>, the two commands <c>jazz trim</c> sends.
    /// </summary>
    [RelayCommand]
    private async Task QuickTrimFileAsync()
    {
        if (_files?.OpenMovie() is not { } chosen)
        {
            return;
        }

        string path = Path.GetFullPath(chosen);
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
    }

    private void UpdateTitle()
    {
        string name = _session.ProjectPath.Length > 0
            ? Path.GetFileNameWithoutExtension(_session.ProjectPath)
            : _session.Project.Name;

        Title = name.Length > 0 ? $"{name} - Jazz Hands" : "Jazz Hands";
    }
}
