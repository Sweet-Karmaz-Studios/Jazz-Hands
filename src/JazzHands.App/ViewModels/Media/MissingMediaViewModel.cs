using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.Core.Commands;
using JazzHands.Engine.Commands;

namespace JazzHands.App.ViewModels.Media;

/// <summary>One missing media item in the dialog, with the files that might be it.</summary>
public sealed partial class MissingMediaRow : ObservableObject
{
    [ObservableProperty]
    private MediaCandidate? _selectedCandidate;

    /// <summary>A row for a missing item.</summary>
    public MissingMediaRow(MissingMediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
        _selectedCandidate = info.Candidates.FirstOrDefault();
    }

    /// <summary>What the search found.</summary>
    public MissingMediaInfo Info { get; }

    /// <summary>The item's name.</summary>
    public string Name => Info.Name;

    /// <summary>Where the project expected it.</summary>
    public string Expected => Info.Expected;

    /// <summary>The files that might be it, best first.</summary>
    public IReadOnlyList<MediaCandidate> Candidates => Info.Candidates;

    /// <summary>What the row says about where it might be.</summary>
    public string Found => Info.Candidates.FirstOrDefault()?.Match switch
    {
        MediaMatch.Hash => "Found: the same file",
        MediaMatch.NameAndSize => "Found: same name and size",
        MediaMatch.Name => "A file of the same name; check it",
        _ => "Not found",
    };

    /// <summary>How many clips need it.</summary>
    public string Clips => Info.Clips == 1 ? "1 clip" : $"{Info.Clips} clips";
}

/// <summary>
/// The missing media dialog: what is missing, where it might be, and relinking it, all of it at
/// once by hash (<c>media.relink --auto</c>) or one at a time by hand.
/// </summary>
public sealed partial class MissingMediaViewModel : ObservableObject
{
    private readonly ISession _session;
    private readonly IFileDialogService _files;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService? _dialogs;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>What the dialog says at the top: what the list is, or that there is nothing to do.</summary>
    [ObservableProperty]
    private string _intro = MissingIntro;

    /// <summary>The top line when files are missing.</summary>
    public const string MissingIntro = "These files are not where the project says. Jazz Hands looked in the project's folder and where they were; add the folder they went to if it is somewhere else.";

    /// <summary>A dialog over a session.</summary>
    public MissingMediaViewModel(ISession session, IFileDialogService files, IUiDispatcher ui, IDialogService? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(ui);
        _session = session;
        _files = files;
        _ui = ui;
        _dialogs = dialogs;
    }

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The missing items.</summary>
    public ObservableCollection<MissingMediaRow> Rows { get; } = [];

    /// <summary>The extra folders searched, besides the project's and where the files were.</summary>
    public ObservableCollection<string> Searches { get; } = [];

    /// <summary>Looks again, off the UI thread: the search hashes files.</summary>
    public async Task LoadAsync()
    {
        IsBusy = true;
        Status = "Looking for the missing files...";
        string[] search = [.. Searches];
        MissingMediaInfo[] missing;
        try
        {
            missing = await Task.Run(() => _session.Query(new FindMissingMediaQuery([.. search]))).ConfigureAwait(true);
        }
        catch (Exception error) when (error is CommandException or InvalidOperationException or System.IO.IOException)
        {
            IsBusy = false;
            Status = error.Message;
            return;
        }

        Rows.Clear();
        foreach (MissingMediaInfo item in missing)
        {
            Rows.Add(new MissingMediaRow(item));
        }

        int found = missing.Count(item => Engine.Library.MediaLibrary.AutoChoice(item) is not null);
        Status = missing.Length == 0
            ? "Nothing is missing."
            : $"{missing.Length} missing; {found} found and can be relinked at once.";
        Intro = missing.Length == 0 ? "Every file this project uses is where it says. There is nothing to relink." : MissingIntro;
        IsBusy = false;
        FindAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Relinks everything found by hash (or by name and size when only one file fits), as one undo.</summary>
    [RelayCommand(CanExecute = nameof(CanFindAll))]
    private async Task FindAllAsync()
    {
        CommandResult result = await _session.ExecuteAsync(new RelinkMediaCommand(Auto: true, Search: [.. Searches])).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
        if (!result.Ok)
        {
            Status = result.Error ?? "Nothing was relinked.";
        }
        else if (Rows.Count == 0)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool CanFindAll() => !IsBusy && Rows.Any(row => Engine.Library.MediaLibrary.AutoChoice(row.Info) is not null);

    /// <summary>Adds a folder to look in, and looks again.</summary>
    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (_files.PickFolder("Where are the missing files?") is { } folder && !Searches.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            Searches.Add(folder);
            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Relinks one row: to its chosen candidate, or a file picked now.</summary>
    [RelayCommand]
    private async Task RelinkAsync(MissingMediaRow? row)
    {
        if (row is null)
        {
            return;
        }

        string? path = row.SelectedCandidate?.Path ?? _files.OpenMedia().FirstOrDefault();
        if (path is null)
        {
            return;
        }

        CommandResult result = await Relinking.RelinkAsync(_session, _dialogs, row.Info.MediaId, path).ConfigureAwait(true);
        if (!result.Ok)
        {
            Status = result.Code == Relinking.DifferentLength ? Relinking.Reason(result.Error) : result.Error ?? "It could not be relinked.";
            return;
        }

        Rows.Remove(row);
        Status = Rows.Count == 0 ? "Nothing is missing." : $"Relinked {row.Name}.";
    }

    /// <summary>Picks a file for a row by hand, and relinks to it.</summary>
    [RelayCommand]
    private async Task BrowseAsync(MissingMediaRow? row)
    {
        if (row is null || _files.OpenMedia().FirstOrDefault() is not { } path)
        {
            return;
        }

        row.SelectedCandidate = new MediaCandidate(path, MediaMatch.Name, 0);
        await RelinkAsync(row).ConfigureAwait(true);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    partial void OnIsBusyChanged(bool value) => _ui.Post(FindAllCommand.NotifyCanExecuteChanged);
}
