using System.IO;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.Core.Commands;
using JazzHands.Engine.Settings;

namespace JazzHands.App.Shell;

/// <summary>
/// The notification area's right-click menu, built afresh each time it opens so it says what is
/// happening now.
/// </summary>
/// <remarks>
/// Open, the last five projects, the running export (which opens the queue), pause or resume the
/// queue, proxies on or off, and Quit. What changes something is a command through the session,
/// like every other menu, so the history says who did it.
/// </remarks>
public sealed class TrayMenu
{
    private const int RecentShown = 5;

    private readonly ISession _session;
    private readonly DesktopStatus _status;
    private readonly AppLifetime _lifetime;
    private readonly SettingsSection<RecentProjects> _recent;
    private readonly Func<bool> _proxiesEnabled;
    private readonly Func<string, Task> _open;
    private readonly Action<string> _showPanel;

    /// <summary>A menu over the editor's parts.</summary>
    /// <param name="session">Where the commands go.</param>
    /// <param name="status">What is running.</param>
    /// <param name="lifetime">Show and quit.</param>
    /// <param name="recent">The recent projects.</param>
    /// <param name="proxiesEnabled">Whether playback uses proxies now.</param>
    /// <param name="open">Opens a project in the window, asking about unsaved work.</param>
    /// <param name="showPanel">Shows the window with a panel in front, by content id.</param>
    public TrayMenu(ISession session, DesktopStatus status, AppLifetime lifetime, SettingsSection<RecentProjects> recent, Func<bool> proxiesEnabled, Func<string, Task> open, Action<string> showPanel)
    {
        _session = session;
        _status = status;
        _lifetime = lifetime;
        _recent = recent;
        _proxiesEnabled = proxiesEnabled;
        _open = open;
        _showPanel = showPanel;
    }

    /// <summary>The items as they should be now.</summary>
    public IReadOnlyList<MenuItemViewModel> Build()
    {
        _status.Refresh();
        var items = new List<MenuItemViewModel>
        {
            new("_Open Jazz Hands", new RelayCommand(_lifetime.Show), toolTip: "Show the window"),
        };

        var recent = new MenuItemViewModel("_Recent projects");
        foreach (string path in _recent.Current.Paths.Take(RecentShown))
        {
            recent.Items.Add(new MenuItemViewModel(Path.GetFileNameWithoutExtension(path), new AsyncRelayCommand(() => _open(path)), toolTip: path));
        }

        if (recent.Items.Count > 0)
        {
            items.Add(recent);
        }

        items.Add(MenuItemViewModel.Separator());
        if (_status.ExportLine.Length > 0)
        {
            items.Add(new MenuItemViewModel(_status.ExportLine, new RelayCommand(() => _showPanel("exportQueue")), toolTip: "Open the export queue"));
        }

        items.Add(_status.QueuePaused
            ? new MenuItemViewModel("_Resume exports", new AsyncRelayCommand(() => _session.ExecuteAsync(new ResumeExportCommand())), toolTip: "Carry on with the paused exports")
            : new MenuItemViewModel("_Pause exports", new AsyncRelayCommand(() => _session.ExecuteAsync(new PauseExportCommand())), toolTip: "Pause every export in the queue"));

        bool proxies = _proxiesEnabled();
        items.Add(new MenuItemViewModel("Play pro_xies", new AsyncRelayCommand(() => _session.ExecuteAsync(new SetProxiesEnabledCommand(!proxies))), isChecked: () => proxies, toolTip: "Play the lighter proxy files instead of their sources"));
        items.Add(MenuItemViewModel.Separator());
        items.Add(new MenuItemViewModel("_Quit Jazz Hands", new AsyncRelayCommand(_lifetime.QuitFromPersonAsync), toolTip: "Quit, after asking about unsaved work and running exports"));
        return items;
    }
}
