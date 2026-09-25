using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;

namespace JazzHands.App.ViewModels.Media;

/// <summary>
/// The media panel's library side (Phase 28): which files are offline or changed, a bar saying so
/// with a way to fix it, relink and replace, removing unused media, and watching a folder.
/// </summary>
/// <remarks>
/// The files are checked off the UI thread whenever the set of media or where it lives changes
/// (<c>media.check</c> hashes each file), and the rows marked offline. Everything that changes the
/// project is a command.
/// </remarks>
public sealed partial class MediaPanelViewModel
{
    private Dictionary<string, MediaFileState> _states = new(StringComparer.Ordinal);
    private string _checked = string.Empty;

    /// <summary>What the library bar says: missing and changed files, or empty when all is well.</summary>
    [ObservableProperty]
    private string _libraryNotice = string.Empty;

    /// <summary>True when the library bar is showing.</summary>
    public bool HasLibraryNotice => LibraryNotice.Length > 0;

    /// <summary>True when some files are missing, so the bar offers to find them.</summary>
    public bool HasMissing => _states.Values.Any(state => state == MediaFileState.Missing);

    /// <summary>True when some files changed on disk, so the bar offers to update them.</summary>
    public bool HasChanged => _states.Values.Any(state => state == MediaFileState.Changed);

    partial void OnLibraryNoticeChanged(string value) => OnPropertyChanged(nameof(HasLibraryNotice));

    /// <summary>Checks the files again when what the project points at has changed since the last look.</summary>
    public async Task CheckFilesAsync()
    {
        Project project = _session.Project;
        string signature = _session.ProjectPath + "|" + string.Join('|', project.Media.Select(item => $"{item.Id}:{item.RelativePath}:{item.Hash}"));
        if (signature == _checked)
        {
            return;
        }

        _checked = signature;
        MediaCheckInfo[] checks;
        try
        {
            checks = await Task.Run(() => _session.Query(new CheckMediaQuery())).ConfigureAwait(true);
        }
        catch (Exception error) when (error is CommandException or InvalidOperationException or System.IO.IOException)
        {
            _log.Debug(error, "The media files could not be checked");
            return;
        }

        _states = checks.ToDictionary(check => check.MediaId, check => check.State, StringComparer.Ordinal);
        foreach (MediaItemViewModel row in _rows.Values)
        {
            row.IsOffline = _states.GetValueOrDefault(row.Id) == MediaFileState.Missing;
        }

        int missing = checks.Count(check => check.State == MediaFileState.Missing);
        int changed = checks.Count(check => check.State == MediaFileState.Changed);
        LibraryNotice = (missing, changed) switch
        {
            (0, 0) => string.Empty,
            (_, 0) => missing == 1 ? "A file is missing: its clips show an offline slate." : $"{missing} files are missing: their clips show an offline slate.",
            (0, _) => changed == 1 ? "A file changed on disk since it was imported." : $"{changed} files changed on disk since they were imported.",
            _ => $"{missing} missing and {changed} changed on disk.",
        };
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(HasChanged));
    }

    /// <summary>Opens the missing media dialog, and checks again after it.</summary>
    [RelayCommand]
    private async Task FindMissingAsync()
    {
        await _dialogs.ShowMissingMediaAsync().ConfigureAwait(true);
        _checked = string.Empty;
        await CheckFilesAsync().ConfigureAwait(true);
    }

    /// <summary>Reads every changed file again, as one undo.</summary>
    [RelayCommand]
    private async Task UpdateChangedAsync()
    {
        ICommand[] reprobes = [.. _states.Where(pair => pair.Value == MediaFileState.Changed).Select(pair => (ICommand)new ReprobeMediaCommand(pair.Key))];
        if (reprobes.Length > 0)
        {
            await RunAsync(new BatchCommand([.. reprobes], "Update changed media"), $"Updated {reprobes.Length} changed file(s).").ConfigureAwait(true);
        }
    }

    /// <summary>Points the selected item at a file picked now: it moved.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RelinkSelectedAsync()
    {
        if (SelectedItem is { } row && _files.OpenMedia().FirstOrDefault() is { } path)
        {
            await RunAsync(new RelinkMediaCommand(row.Id, path), $"Relinked {row.Name}.").ConfigureAwait(true);
        }
    }

    /// <summary>Swaps the selected item's file for another, keeping its clips.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ReplaceSelectedAsync()
    {
        if (SelectedItem is { } row && _files.OpenMedia().FirstOrDefault() is { } path)
        {
            await RunAsync(new ReplaceMediaCommand(row.Id, path), $"Replaced {row.Name}'s file.").ConfigureAwait(true);
        }
    }

    /// <summary>Takes out every item no clip uses.</summary>
    [RelayCommand]
    private Task RemoveUnusedAsync() => RunAsync(new RemoveUnusedMediaCommand(), "Removed the media no clip uses.");

    /// <summary>Watches a folder for new recordings, asking for the tags to give them.</summary>
    [RelayCommand]
    private async Task WatchFolderAsync()
    {
        if (_files.PickFolder("Watch this folder for new recordings") is not { } folder)
        {
            return;
        }

        string? tags = await _dialogs.AskForTextAsync("Watch a folder", "Tags for everything it brings in, separated by commas:", "capture").ConfigureAwait(true);
        if (tags is null)
        {
            return;
        }

        EquatableArray<string> list = [.. tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        await RunAsync(new WatchMediaCommand(folder, list, Bin: "Captures"), $"Watching {folder}: new recordings come in as they finish.").ConfigureAwait(true);
    }

    /// <summary>Stops every watch.</summary>
    [RelayCommand]
    private Task StopWatchingAsync() => RunAsync(new UnwatchMediaCommand(), "Stopped watching.");
}
