using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.Core.Commands;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using Path = System.IO.Path;

namespace JazzHands.App.ViewModels.Media;

/// <summary>
/// The Consolidate and Archive dialog: where to gather the project and its media, whether to keep
/// only what clips use, and whether to move instead of copy. It sends
/// <c>project.consolidate</c> or <c>project.archive</c>.
/// </summary>
public sealed partial class ConsolidateViewModel : ObservableObject
{
    private readonly ISession _session;
    private readonly IFileDialogService _files;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private string _destination = string.Empty;

    [ObservableProperty]
    private bool _trim;

    [ObservableProperty]
    private double _handleSeconds = 1.0;

    [ObservableProperty]
    private bool _move;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>A dialog over a session.</summary>
    public ConsolidateViewModel(ISession session, IFileDialogService files)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(files);
        _session = session;
        _files = files;
    }

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>True for Archive (a zip), false for Consolidate (a folder).</summary>
    public bool IsArchive { get; private set; }

    /// <summary>The dialog's title.</summary>
    public string Title => IsArchive ? "Archive the project" : "Consolidate the project";

    /// <summary>What the destination is.</summary>
    public string DestinationLabel => IsArchive ? "Zip file" : "Folder";

    /// <summary>The main button: what pressing it does.</summary>
    public string RunLabel => IsArchive ? "Archive" : "Consolidate";

    /// <summary>Sets the dialog up for one or the other, with a destination beside the project.</summary>
    public void Load(bool archive)
    {
        IsArchive = archive;
        string name = _session.Project.Name.Length > 0 ? _session.Project.Name : "project";
        string near = _session.ProjectPath.Length > 0 ? Path.GetDirectoryName(Path.GetFullPath(_session.ProjectPath))! : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Destination = archive ? Path.Combine(near, name + ".zip") : Path.Combine(near, name + " (consolidated)");
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(DestinationLabel));
        OnPropertyChanged(nameof(RunLabel));
    }

    partial void OnTrimChanged(bool value)
    {
        if (value)
        {
            Move = false;
        }
    }

    partial void OnMoveChanged(bool value)
    {
        if (value)
        {
            Trim = false;
        }
    }

    [RelayCommand]
    private void Browse()
    {
        string? chosen = IsArchive ? _files.SaveArchive(Destination) : _files.PickFolder("Gather the project into");
        if (chosen is not null)
        {
            Destination = chosen;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsBusy = true;
        Status = IsArchive ? "Writing the archive..." : "Gathering the project...";
        Flicks handles = Flicks.FromSeconds(Math.Max(0, HandleSeconds));
        ICommand command = IsArchive
            ? new ArchiveProjectCommand(Destination, Trim, handles)
            : new ConsolidateProjectCommand(Destination, Trim, handles, Move);

        CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(true);
        IsBusy = false;
        if (!result.Ok)
        {
            Status = result.Error ?? "It did not work.";
            return;
        }

        Status = string.Create(CultureInfo.InvariantCulture, $"Done: {Destination}");
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanRun() => !IsBusy && Destination.Trim().Length > 0;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
