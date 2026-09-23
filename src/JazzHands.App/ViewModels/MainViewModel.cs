using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.App.ViewModels.Audio;
using JazzHands.App.ViewModels.Media;
using JazzHands.App.ViewModels.Playback;
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

    [ObservableProperty]
    private string _title = "Jazz Hands";

    /// <summary>Creates the window's viewmodel.</summary>
    public MainViewModel(
        ISession session,
        MediaPanelViewModel media,
        IUiDispatcher ui,
        MetersPanelViewModel? meters = null,
        PreviewPanelViewModel? preview = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        Media = media;
        Meters = meters;
        Preview = preview;
        Panels = [media];

        if (meters is not null)
        {
            Panels.Add(meters);
        }

        if (preview is not null)
        {
            Panels.Add(preview);
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

    /// <summary>Every dockable panel, in the order they were registered.</summary>
    public ObservableCollection<ToolViewModel> Panels { get; }

    [RelayCommand]
    private static void Exit() => Application.Current?.Shutdown();

    private void UpdateTitle()
    {
        string name = _session.ProjectPath.Length > 0
            ? Path.GetFileNameWithoutExtension(_session.ProjectPath)
            : _session.Project.Name;

        Title = name.Length > 0 ? $"{name} - Jazz Hands" : "Jazz Hands";
    }
}
