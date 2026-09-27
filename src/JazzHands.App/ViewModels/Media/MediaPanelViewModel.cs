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

    /// <summary>Tiles with a thumbnail that scrubs through the file under the mouse.</summary>
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

    /// <summary>What the proxy suggestion bar says, or empty when it is hidden.</summary>
    [ObservableProperty]
    private string _proxySuggestion = string.Empty;

    private readonly IMediaImagery? _imagery;
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private List<string> _suggested = [];

    /// <summary>Creates the panel.</summary>
    /// <param name="session">The session every action goes through.</param>
    /// <param name="dialogs">The import dialog.</param>
    /// <param name="files">The file picker.</param>
    /// <param name="ui">The UI thread.</param>
    /// <param name="imagery">Where tile pictures come from; none in a test that does not care.</param>
    public MediaPanelViewModel(ISession session, IDialogService dialogs, IFileDialogService files, IUiDispatcher ui, IMediaImagery? imagery = null)
        : base(PanelContentId, "Media")
    {
        ArgumentNullException.ThrowIfNull(session);

        _session = session;
        _dialogs = dialogs;
        _files = files;
        _ui = ui;
        _imagery = imagery;

        Root = new MediaFolderViewModel(string.Empty, "All media");
        Folders = [Root];

        // What is in the project when it opens has been seen; only new arrivals are suggested for proxies.
        _known.UnionWith(session.Project.Media.Select(item => item.Id));

        _session.ProjectChanged += OnProjectChanged;
        _imagery?.Changed += (_, _) => UpdateThumbnails();

        Refresh();
    }

    /// <summary>True when the proxy suggestion bar is showing.</summary>
    public bool HasProxySuggestion => ProxySuggestion.Length > 0;

    /// <summary>The mouse is over a tile, this far across it: show the frame there.</summary>
    public void ScrubTo(MediaItemViewModel row, double fraction)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.ScrubFraction = Math.Clamp(fraction, 0.0, 1.0);
        if (_imagery?.At(row.Item, row.ScrubFraction.Value) is { } picture)
        {
            row.Thumbnail = picture;
        }
    }

    /// <summary>The mouse left a tile: back to its poster.</summary>
    public void EndScrub(MediaItemViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.ScrubFraction = null;
        row.Thumbnail = _imagery?.Poster(row.Item) ?? row.Thumbnail;
    }

    /// <summary>Asks for every shown tile's picture, and puts up what is ready.</summary>
    /// <remarks>Only in the grid; the list has no pictures and asks for none.</remarks>
    public void UpdateThumbnails()
    {
        if (_imagery is null || !IsGridMode)
        {
            return;
        }

        foreach (MediaItemViewModel row in Items)
        {
            System.Windows.Media.ImageSource? picture = row.ScrubFraction is { } fraction
                ? _imagery.At(row.Item, fraction)
                : _imagery.Poster(row.Item);

            if (picture is not null && !ReferenceEquals(picture, row.Thumbnail))
            {
                row.Thumbnail = picture;
            }
        }
    }

    /// <inheritdoc />
    partial void OnProxySuggestionChanged(string value) => OnPropertyChanged(nameof(HasProxySuggestion));

    /// <summary>The rows the list is showing, after the folder and the search have had their say.</summary>
    public ObservableList<MediaItemViewModel> Items { get; } = [];

    /// <summary>The folder tree, which is one root with everything under it.</summary>
    public ObservableCollection<MediaFolderViewModel> Folders { get; }

    /// <summary>The root of the folder tree.</summary>
    public MediaFolderViewModel Root { get; }

    /// <summary>How many items the project has, whatever the filter is showing.</summary>
    public int TotalCount => _all.Count;

    /// <summary>True when the project has no media: the list says how to bring some in.</summary>
    public bool IsEmpty => _all.Count == 0;

    /// <summary>What the status bar says about how much of the bin is on screen.</summary>
    public string CountSummary => Items.Count == TotalCount
        ? (TotalCount == 1 ? "1 item" : $"{TotalCount} items")
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
        OnPropertyChanged(nameof(IsEmpty));
        SuggestProxies();

        foreach (MediaItemViewModel row in rebuilt)
        {
            row.IsOffline = _states.GetValueOrDefault(row.Id) == MediaFileState.Missing;
        }

        _ = CheckFilesAsync();
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
        UpdateThumbnails();
    }

    /// <inheritdoc />
    partial void OnSelectedItemChanged(MediaItemViewModel? value)
    {
        RemoveCommand.NotifyCanExecuteChanged();
        ReprobeCommand.NotifyCanExecuteChanged();
        SetColorCommand.NotifyCanExecuteChanged();
        RevealCommand.NotifyCanExecuteChanged();
        GenerateProxyCommand.NotifyCanExecuteChanged();
        QuickTrimCommand.NotifyCanExecuteChanged();
        FindSceneCutsCommand.NotifyCanExecuteChanged();
        OpenInSourceCommand.NotifyCanExecuteChanged();
        RelinkSelectedCommand.NotifyCanExecuteChanged();
        ReplaceSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Files dropped on the panel from Explorer: the Import dialog for them, as Ctrl+I's picker
    /// would open it. A folder brings the media files in it, one level deep.
    /// </summary>
    public Task ImportDroppedAsync(IReadOnlyList<string> dropped)
    {
        ArgumentNullException.ThrowIfNull(dropped);
        string[] paths = [.. DroppedMedia(dropped)];
        return paths.Length == 0 ? Task.CompletedTask : _dialogs.ShowImportAsync(paths);
    }

    /// <summary>The files a drop brings: files as they are, and the media files in a dropped folder.</summary>
    internal static IEnumerable<string> DroppedMedia(IEnumerable<string> dropped)
    {
        foreach (string path in dropped)
        {
            if (System.IO.Directory.Exists(path))
            {
                foreach (string file in System.IO.Directory.EnumerateFiles(path).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (Engine.Library.MediaWatchService.Extensions.Contains(System.IO.Path.GetExtension(file)))
                    {
                        yield return file;
                    }
                }
            }
            else if (System.IO.File.Exists(path))
            {
                yield return path;
            }
        }
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
    private async Task GenerateProxyAsync()
    {
        if (SelectedItem is not { } row)
        {
            return;
        }

        await RunAsync(new GenerateProxyCommand(row.Id), $"Making a proxy of {row.Name} on the export queue.").ConfigureAwait(true);
    }

    /// <summary>
    /// Makes a multicam clip of the selected recordings (Phase 41), synced by their sound, at the
    /// end of the active sequence's first picture track.
    /// </summary>
    [RelayCommand]
    private Task MakeMulticamAsync(System.Collections.IList? selected)
    {
        string[] ids = [.. (selected?.OfType<MediaItemViewModel>() ?? []).Select(row => row.Id)];
        if (ids.Length < 2)
        {
            Status = "Select two recordings or more of the same moment, then Make multicam.";
            return Task.CompletedTask;
        }

        return RunAsync(new CreateMulticamCommand([.. ids]), $"Made a multicam of {ids.Length} recordings. Right-click it on the timeline to show the angles.");
    }

    /// <summary>Opens the selected item in the source monitor, to mark and edit in from (Phase 38).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task OpenInSourceAsync() => SelectedItem is { } row
        ? RunAsync(new OpenSourceCommand(row.Id), string.Empty)
        : Task.CompletedTask;

    /// <summary>The scene cuts dialog for the selected item: find its shots, then mark them or make subclips (Phase 37).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task FindSceneCutsAsync() => SelectedItem is { } row
        ? _dialogs.ShowSceneCutsAsync(row.Id, clipId: null)
        : Task.CompletedTask;

    /// <summary>Quick Trim this recording: a sequence of its own to keep and cut stretches of (<c>trim.start</c>).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task QuickTrimAsync()
    {
        if (SelectedItem is not { } row)
        {
            return;
        }

        await RunAsync(new StartTrimCommand(row.Id), $"Quick Trim of {row.Name}: mark in and out, Enter keeps, Backspace cuts.").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task MakeSuggestedProxiesAsync()
    {
        List<string> ids = _suggested;
        _suggested = [];
        ProxySuggestion = string.Empty;

        foreach (string id in ids)
        {
            await RunAsync(new GenerateProxyCommand(id), $"Making {ids.Count} {(ids.Count == 1 ? "proxy" : "proxies")} on the export queue.").ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void DismissProxySuggestion()
    {
        _suggested = [];
        ProxySuggestion = string.Empty;
    }

    /// <summary>
    /// New media heavy enough to want a proxy, and without one: put the suggestion bar up. The
    /// command line gets the same advice in its log; see the media.add handler.
    /// </summary>
    private void SuggestProxies()
    {
        List<string> arrivals = [.. _session.Project.Media.Select(item => item.Id).Where(id => !_known.Contains(id))];
        if (arrivals.Count == 0)
        {
            return;
        }

        _known.UnionWith(arrivals);

        ProxyInfo[] proxies;
        try
        {
            proxies = _session.Query(new ListProxiesQuery());
        }
        catch (Exception error) when (error is CommandException or InvalidOperationException)
        {
            _log.Debug(error, "Could not ask which new media wants a proxy");
            return;
        }

        List<ProxyInfo> wanted = [.. proxies.Where(proxy => proxy.Suggested && proxy.State == ProxyState.None && arrivals.Contains(proxy.MediaId))];
        if (wanted.Count == 0)
        {
            return;
        }

        _suggested = [.. _suggested.Union(wanted.Select(proxy => proxy.MediaId))];
        ProxySuggestion = _suggested.Count == 1
            ? $"{wanted[0].Name} is heavy to decode (4K, AV1 or 10-bit HEVC). A proxy would make it edit smoothly."
            : $"{_suggested.Count} new files are heavy to decode (4K, AV1 or 10-bit HEVC). Proxies would make them edit smoothly.";
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
        UpdateThumbnails();
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
