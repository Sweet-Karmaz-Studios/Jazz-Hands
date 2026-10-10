using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Core.Commands;
using JazzHands.Core.Export;
using JazzHands.Engine.Export;
using Path = System.IO.Path;

namespace JazzHands.App.ViewModels.Export;

/// <summary>
/// The Export Queue panel: every job, where it has got to, what it has done, and ways to stop,
/// hold and reorder it.
/// </summary>
/// <remarks>
/// It reads the queue and hears its progress, but cancelling, pausing, resuming, reordering and
/// clearing are commands (<c>export.cancel</c>, <c>export.pause</c> and the rest), so a job paused
/// here is paused the way Claude Code would pause it. Progress arrives on the export threads about
/// four times a second a job and is folded into one update per job on the UI thread.
///
/// A job that finishes or fails says so in <see cref="Notice"/>, which the main window shows where
/// the person is looking, and the taskbar button carries the progress of everything running.
/// </remarks>
public sealed partial class ExportQueuePanelViewModel : ToolViewModel
{
    private readonly ISession _session;
    private readonly IExportService? _queue;
    private readonly IUiDispatcher _ui;
    private readonly Dictionary<string, ExportJobInfo> _pending = new(StringComparer.Ordinal);
    private bool _flushQueued;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private ExportJobViewModel? _selectedJob;

    [ObservableProperty]
    private double _taskbarProgress;

    [ObservableProperty]
    private TaskbarItemProgressState _taskbarState = TaskbarItemProgressState.None;

    /// <summary>Creates the panel.</summary>
    /// <param name="session">Where the queue commands go.</param>
    /// <param name="queue">The queue, or null in a host without one.</param>
    /// <param name="ui">The UI thread.</param>
    public ExportQueuePanelViewModel(ISession session, IExportService? queue, IUiDispatcher ui)
        : base("exportQueue", "Export Queue")
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        _queue = queue;
        _ui = ui;

        if (queue is not null)
        {
            queue.Changed += (_, job) => Queue(job);
            foreach (ExportJobInfo job in queue.List())
            {
                Jobs.Add(new ExportJobViewModel(this, job));
            }
        }

        UpdateStatus();
    }

    /// <summary>Raised on the UI thread when a job finishes or fails, with a sentence saying so.</summary>
    public event EventHandler<string>? Notice;

    /// <summary>Every job, oldest first.</summary>
    public ObservableCollection<ExportJobViewModel> Jobs { get; } = [];

    /// <summary>What the selected job has done, a line a step.</summary>
    public ObservableCollection<string> Log { get; } = [];

    /// <summary>True when there is nothing in the queue, for the empty state.</summary>
    public bool IsEmpty => Jobs.Count == 0;

    /// <summary>Stops a job through the command every surface uses.</summary>
    internal Task CancelAsync(string jobId) => RunAsync(new CancelExportCommand(jobId));

    /// <summary>Holds or lets go of a job.</summary>
    internal Task PauseOrResumeAsync(ExportJobViewModel job) =>
        RunAsync(job.State == ExportJobState.Paused ? new ResumeExportCommand(job.Id) : new PauseExportCommand(job.Id));

    /// <summary>Moves a job up or down the queue.</summary>
    internal Task SetPriorityAsync(string jobId, ExportPriority priority) => RunAsync(new SetExportPriorityCommand(jobId, priority));

    partial void OnSelectedJobChanged(ExportJobViewModel? value) => RefreshLog();

    [RelayCommand]
    private async Task ClearFinishedAsync()
    {
        CommandResult result = await _session.ExecuteAsync(new ClearExportsCommand()).ConfigureAwait(true);
        if (!result.Ok)
        {
            Status = result.Error ?? string.Empty;
            return;
        }

        for (int index = Jobs.Count - 1; index >= 0; index--)
        {
            if (Jobs[index].IsFinished)
            {
                Jobs.RemoveAt(index);
            }
        }

        UpdateStatus();
    }

    [RelayCommand]
    private Task PauseAllAsync() => RunAsync(new PauseExportCommand());

    [RelayCommand]
    private Task ResumeAllAsync() => RunAsync(new ResumeExportCommand());

    private async Task RunAsync(ICommand command)
    {
        CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(true);
        if (!result.Ok)
        {
            Status = result.Error ?? string.Empty;
        }
    }

    private void Queue(ExportJobInfo job)
    {
        lock (_pending)
        {
            _pending[job.Id] = job;
            if (_flushQueued)
            {
                return;
            }

            _flushQueued = true;
        }

        _ui.Post(Flush);
    }

    private void Flush()
    {
        ExportJobInfo[] updates;
        lock (_pending)
        {
            updates = [.. _pending.Values];
            _pending.Clear();
            _flushQueued = false;
        }

        foreach (ExportJobInfo update in updates)
        {
            ExportJobViewModel? existing = Jobs.FirstOrDefault(job => string.Equals(job.Id, update.Id, StringComparison.Ordinal));
            if (existing is null)
            {
                Jobs.Add(new ExportJobViewModel(this, update));
                continue;
            }

            ExportJobState before = existing.State;
            existing.Update(update);
            if (before != update.State)
            {
                Announce(existing, update);
            }
        }

        if (SelectedJob is { } selected && updates.Any(update => string.Equals(update.Id, selected.Id, StringComparison.Ordinal)))
        {
            RefreshLog();
        }

        UpdateStatus();
    }

    private void Announce(ExportJobViewModel job, ExportJobInfo update)
    {
        string? text = update.State switch
        {
            ExportJobState.Done => string.Create(CultureInfo.InvariantCulture, $"Exported {job.Name}: {update.Bytes / 1048576.0:F1} MB."),
            ExportJobState.Failed => $"{job.Name} did not export: {update.Error}",
            _ => null,
        };

        if (text is not null)
        {
            Notice?.Invoke(this, text);
        }
    }

    private void RefreshLog()
    {
        Log.Clear();
        if (SelectedJob is not { } job || _queue is null)
        {
            return;
        }

        foreach (string line in _queue.Log(job.Id) ?? [])
        {
            Log.Add(line);
        }
    }

    private void UpdateStatus()
    {
        int running = Jobs.Count(job => job.State == ExportJobState.Running);
        int waiting = Jobs.Count(job => job.State == ExportJobState.Queued);
        int paused = Jobs.Count(job => job.State == ExportJobState.Paused);

        Status = _queue is null
            ? "There is no export queue in this window."
            : running + waiting + paused == 0
                ? Jobs.Count == 0 ? "Nothing queued. Export from File, or press Export on a Quick Trim." : "Everything has finished."
                : string.Create(CultureInfo.InvariantCulture, $"{running} exporting, {waiting} waiting{(paused > 0 ? $", {paused} paused" : string.Empty)}.");

        ExportJobViewModel[] active = [.. Jobs.Where(job => job.State == ExportJobState.Running)];
        TaskbarProgress = active.Length == 0 ? 0 : active.Average(job => job.Progress);
        TaskbarState = active.Length > 0 ? TaskbarItemProgressState.Normal
            : paused > 0 ? TaskbarItemProgressState.Paused
            : TaskbarItemProgressState.None;

        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>One job in the Export Queue panel.</summary>
public sealed partial class ExportJobViewModel : ObservableObject
{
    private readonly ExportQueuePanelViewModel _panel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseOrResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RaiseCommand))]
    private ExportJobState _state;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private string _stateText = string.Empty;

    [ObservableProperty]
    private ExportPriority _priority;

    internal ExportJobViewModel(ExportQueuePanelViewModel panel, ExportJobInfo job)
    {
        _panel = panel;
        Id = job.Id;
        OutputPath = job.OutputPath;
        Label = job.Label;
        Update(job);
    }

    /// <summary>The job id.</summary>
    public string Id { get; }

    /// <summary>The file it writes.</summary>
    public string OutputPath { get; }

    /// <summary>What the job is called, when its file name says nothing: "Proxy of" a file.</summary>
    public string? Label { get; }

    /// <summary>What the row shows: the label, or the file's name.</summary>
    public string Name => Label ?? Path.GetFileName(OutputPath);

    /// <summary>True once it will not change again.</summary>
    public bool IsFinished => State is ExportJobState.Done or ExportJobState.Failed or ExportJobState.Cancelled;

    /// <summary>True for a failure, which the row shows in the warning colour.</summary>
    public bool IsFailed => State == ExportJobState.Failed;

    /// <summary>True while it is held.</summary>
    public bool IsPaused => State == ExportJobState.Paused;

    /// <summary>The pause button's glyph: play to resume a held job, pause otherwise.</summary>
    public string PauseGlyph => IsPaused ? "" : "";

    /// <summary>What the pause button does, for its tooltip and screen readers.</summary>
    public string PauseText => IsPaused ? "Resume this export" : "Pause this export";

    /// <summary>Takes a new report.</summary>
    internal void Update(ExportJobInfo job)
    {
        State = job.State;
        Progress = job.Progress;
        Priority = job.Priority;
        StateText = job.State switch
        {
            ExportJobState.Queued => job.Priority == ExportPriority.Normal ? "Waiting" : $"Waiting, {job.Priority.ToString().ToLowerInvariant()}",
            ExportJobState.Running => string.Create(CultureInfo.InvariantCulture, $"{job.Progress * 100:F0}%{(job.Hardware ? " GPU" : string.Empty)}"),
            ExportJobState.Paused => "Paused",
            ExportJobState.Done => "Done",
            ExportJobState.Failed => "Failed",
            _ => "Cancelled",
        };

        Detail = job.State switch
        {
            ExportJobState.Failed => job.Error ?? "It failed without saying why.",
            ExportJobState.Running when job.TotalFrames > 0 => string.Create(
                CultureInfo.InvariantCulture,
                $"{job.Mode.ToString().ToLowerInvariant()}, {job.Encoder}, frame {job.Frame} of {job.TotalFrames}, {job.Fps:F0} fps{(job.EtaSeconds is { } eta ? $", {eta:F0} s left" : string.Empty)}"),
            ExportJobState.Running => $"{job.Mode.ToString().ToLowerInvariant()}, {job.Bytes / 1048576.0:F1} MB written",
            ExportJobState.Done => string.Create(
                CultureInfo.InvariantCulture,
                $"{job.Bytes / 1048576.0:F1} MB, {job.Encoder}{(job.Note is { Length: > 0 } note ? $". {note}" : string.Empty)}"),
            _ => $"{job.Preset}, {job.Mode.ToString().ToLowerInvariant()}{(job.Note is { Length: > 0 } waiting ? $". {waiting}" : string.Empty)}",
        };

        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseGlyph));
        OnPropertyChanged(nameof(PauseText));
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private Task CancelAsync() => _panel.CancelAsync(Id);

    private bool CanCancel() => !IsFinished;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private Task PauseOrResumeAsync() => _panel.PauseOrResumeAsync(this);

    private bool CanPause() => State is ExportJobState.Queued or ExportJobState.Running or ExportJobState.Paused;

    /// <summary>Moves a waiting job to the front of the queue, or back to normal from there.</summary>
    [RelayCommand(CanExecute = nameof(CanRaise))]
    private Task RaiseAsync() => _panel.SetPriorityAsync(Id, Priority == ExportPriority.High ? ExportPriority.Normal : ExportPriority.High);

    private bool CanRaise() => State is ExportJobState.Queued or ExportJobState.Paused;

    [RelayCommand]
    private void ShowInFolder()
    {
        // Explorer with the file selected; if it is not there yet, the folder.
        string arguments = System.IO.File.Exists(OutputPath) ? $"/select,\"{OutputPath}\"" : $"\"{Path.GetDirectoryName(OutputPath)}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }
}
