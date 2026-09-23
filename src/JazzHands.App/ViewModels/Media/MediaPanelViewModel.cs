using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Engine.Commands;
using Serilog;
using Path = System.IO.Path;

namespace JazzHands.App.ViewModels.Media;

/// <summary>How the bin draws its items.</summary>
public enum MediaViewMode
{
    /// <summary>A table with columns.</summary>
    List,

    /// <summary>Tiles with a thumbnail. The thumbnail itself arrives in Phase 14.</summary>
    Grid,
}

/// <summary>
/// The media panel: the project's bin, its folders, and everything that can be done to a file
/// without putting it on a timeline.
/// </summary>
/// <remarks>
/// Every action here ends in a command. The panel never edits the project, so the same bin
/// operations are available from <c>jazz</c>, from JSON-RPC and from MCP, and undo works the same
/// whichever of them did it.
/// </remarks>
public sealed partial class MediaPanelViewModel : ToolViewModel
{
    /// <summary>The panel's content id in the docking layout.</summary>
    public const string PanelContentId = "media";

    private readonly ILogger _log = Log.ForContext<MediaPanelViewModel>();
    private readonly ISession _session;
    private readonly IDialogService _dialogs;
    private readonly IFileDialogService _files;
    private readonly IUiDispatcher _ui;
    private readonly Dictionary<string, MediaItemViewModel> _rows = new(StringComparer.Ordinal);

    private List<MediaItemViewModel> _all = [];
    private long _shownVersion = -1;

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    private MediaViewMode _viewMode = MediaViewMode.List;

    [ObservableProperty]
    private MediaFolderViewModel? _selectedFolder;

    [ObservableProperty]
    private MediaItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Creates the panel.</summary>
    public MediaPanelViewModel(ISession session, IDialogService dialogs, IFileDialogService files, IUiDispatcher ui)
        : base(PanelContentId, "Media")
    {
        ArgumentNullException.ThrowIfNull(session);

        _session = session;
        _dialogs = dialogs;
        _files = files;
        _ui = ui;

        Root = new MediaFolderViewModel(string.Empty, "All media");
        Folders = [Root];

        _session.ProjectChanged += OnProjectChanged;
        Refresh();
    }

    /// <summary>The rows the list is showing, after the folder and the search have had their say.</summary>
    public ObservableList<MediaItemViewModel> Items { get; } = [];

    /// <summary>The folder tree, which is one root with everything under it.</summary>
    public ObservableCollection<MediaFolderViewModel> Folders { get; }

    /// <summary>The root of the folder tree.</summary>
    public MediaFolderViewModel Root { get; }

    /// <summary>How many items the project has, whatever the filter is showing.</summary>
    public int TotalCount => _all.Count;

    /// <summary>What the status bar says about how much of the bin is on screen.</summary>
    public string CountSummary => Items.Count == TotalCount
        ? $"{TotalCount} items"
        : $"{Items.Count} of {TotalCount} items";

    /// <summary>True when the table is showing. Bound to the list's visibility.</summary>
    public bool IsListMode => ViewMode == MediaViewMode.List;

    /// <summary>True when the tiles are showing.</summary>
    public bool IsGridMode => ViewMode == MediaViewMode.Grid;

    /// <summary>Rebuilds everything from the current project snapshot.</summary>
    /// <remarks>
    /// Rows are reused by media id, so a change to one item does not throw away the other one
    /// hundred and ninety nine view models and their selection with them.
    /// </remarks>
    public void Refresh()
    {
        Project project = _session.Project;
        var rebuilt = new List<MediaItemViewModel>(project.Media.Length);

        foreach (MediaItem item in project.Media)
        {
            if (_rows.TryGetValue(item.Id, out MediaItemViewModel? row))
            {
                if (!ReferenceEquals(row.Item, item))
                {
                    // The record changed, so the derived strings did too. Cheaper and less
                    // error prone than working out which of them to raise.
                    row = new MediaItemViewModel(item);
                    _rows[item.Id] = row;
                }
            }
            else
            {
                row = new MediaItemViewModel(item);
                _rows[item.Id] = row;
            }

            rebuilt.Add(row);
        }

        foreach (string gone in _rows.Keys.Where(id => !project.Media.Any(item => item.Id == id)).ToList())
        {
            _rows.Remove(gone);
        }

        _all = [.. rebuilt.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)];

        BuildFolders();
        ApplyFilter();
        OnPropertyChanged(nameof(TotalCount));
    }

    /// <summary>The ids a drag out of the panel should carry.</summary>
    public IReadOnlyList<string> DragIds(IEnumerable<MediaItemViewModel> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return [.. selection.Select(row => row.Id)];
    }

    /// <summary>The files behind a selection, so a drag into another application does something.</summary>
    public IReadOnlyList<string> DragPaths(IEnumerable<MediaItemViewModel> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        return
        [
            .. selection
                .Select(row => ProjectPaths.Resolve(_session.ProjectPath, row.Item.RelativePath))
                .Where(path => !path.Contains('%', StringComparison.Ordinal)),
        ];
    }

    /// <inheritdoc />
    partial void OnSearchChanged(string value) => ApplyFilter();

    /// <inheritdoc />
    partial void OnSelectedFolderChanged(MediaFolderViewModel? value) => ApplyFilter();

    /// <inheritdoc />
    partial void OnViewModeChanged(MediaViewMode value)
    {
        OnPropertyChanged(nameof(IsListMode));
        OnPropertyChanged(nameof(IsGridMode));
    }

    /// <inheritdoc />
    partial void OnSelectedItemChanged(MediaItemViewModel? value)
    {
        RemoveCommand.NotifyCanExecuteChanged();
        ReprobeCommand.NotifyCanExecuteChanged();
        SetColorCommand.NotifyCanExecuteChanged();
        RevealCommand.NotifyCanExecuteChanged();
        GenerateProxyCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        IReadOnlyList<string> paths = _files.OpenMedia();
        if (paths.Count == 0)
        {
            return;
        }

        await _dialogs.ShowImportAsync(paths).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveAsync()
    {
        if (SelectedItem is not { } row)
        {
            return;
        }

        await RunAsync(new RemoveMediaCommand(row.Id), $"Removed {row.Name}.").ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ReprobeAsync()
    {
        if (SelectedItem is not { } row)
        {
            return;
        }

        await RunAsync(new ReprobeMediaCommand(row.Id), $"Re-read {row.Name}.").ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SetColorAsync(string? color)
    {
        if (SelectedItem is not { } row)
        {
            return;
        }

        await RunAsync(
            new SetMediaCommand(row.Id, Color: color ?? string.Empty),
            color is { Length: > 0 } ? $"Labelled {row.Name} {color}." : $"Cleared the label on {row.Name}.")
            .ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Reveal()
    {
        if (SelectedItem is not { } row)
        {
            return;
        }

        string path = ProjectPaths.Resolve(_session.ProjectPath, row.Item.RelativePath);

        try
        {
            // A sequence's path is a pattern, so show the folder rather than a file that is not
            // there under that name.
            bool pattern = path.Contains('%', StringComparison.Ordinal);
            string argument = pattern
                ? $"\"{Path.GetDirectoryName(path)}\""
                : $"/select,\"{path}\"";

            using var explorer = new Process();
            explorer.StartInfo = new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true };
            explorer.Start();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = $"Could not open a window on {path}.";
            _log.Warning(error, "Could not reveal {Path}", path);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void GenerateProxy()
    {
        // Phase 14 owns the proxy cache. The menu item exists now so the panel's shape does not
        // change when it arrives, and it says what it is rather than doing nothing quietly.
        Status = "Proxies arrive in the caching phase.";
    }

    [RelayCommand]
    private void ClearSearch() => Search = string.Empty;

    private bool HasSelection() => SelectedItem is not null;

    private async Task RunAsync(ICommand command, string done)
    {
        try
        {
            CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(true);
            Status = result.Ok ? done : result.Error ?? "The command failed.";
        }
        catch (Exception error) when (error is CommandException or InvalidOperationException)
        {
            Status = error.Message;
            _log.Warning(error, "{Command} failed from the media panel", command.GetType().Name);
        }
    }

    private void OnProjectChanged(object? sender, ProjectChangedEventArgs e)
    {
        // Engine thread. Coalesce by version so a batch of a hundred imports rebuilds the list
        // once rather than a hundred times.
        long version = e?.Version ?? 0;
        if (Interlocked.Exchange(ref _shownVersion, version) == version)
        {
            return;
        }

        _ui.Post(Refresh);
    }

    private void ApplyFilter()
    {
        string[] terms = Search
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string folder = SelectedFolder?.Path ?? string.Empty;

        var shown = new List<MediaItemViewModel>(_all.Count);
        foreach (MediaItemViewModel row in _all)
        {
            if (folder.Length > 0 && !InFolder(row.Folder, folder))
            {
                continue;
            }

            if (terms.Length > 0 && !row.Matches(terms))
            {
                continue;
            }

            shown.Add(row);
        }

        Items.Reset(shown);
        OnPropertyChanged(nameof(CountSummary));
    }

    private static bool InFolder(string itemFolder, string selected) =>
        itemFolder.Equals(selected, StringComparison.OrdinalIgnoreCase) ||
        itemFolder.StartsWith(selected + "/", StringComparison.OrdinalIgnoreCase);

    private void BuildFolders()
    {
        var nodes = new Dictionary<string, MediaFolderViewModel>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = Root,
        };

        Root.Children.Clear();
        Root.Count = _all.Count;

        foreach (string path in _all
            .Select(row => row.Folder)
            .Where(folder => folder.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string built = string.Empty;

            foreach (string segment in segments)
            {
                string parent = built;
                built = built.Length == 0 ? segment : $"{built}/{segment}";

                if (nodes.ContainsKey(built))
                {
                    continue;
                }

                var node = new MediaFolderViewModel(built, segment);
                nodes[built] = node;
                nodes[parent].Children.Add(node);
            }
        }

        foreach ((string path, MediaFolderViewModel node) in nodes)
        {
            if (path.Length > 0)
            {
                node.Count = _all.Count(row => InFolder(row.Folder, path));
            }
        }
    }
}
