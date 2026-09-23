using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
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
/// The Export Queue panel: every job, where it has got to, and a way to stop it.
/// </summary>
/// <remarks>
/// It reads the queue and hears its progress, but cancelling and clearing are commands
/// (<c>export.cancel</c>, <c>export.clear</c>), so a job cancelled here is cancelled the way
/// Claude Code would cancel it. Progress arrives on the export thread about four times a second
/// and is folded into one update per job on the UI thread.
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

    /// <summary>Creates the panel.</summary>
    /// <param name="session">Where the cancel and clear commands go.</param>
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

    /// <summary>Every job, oldest first.</summary>
    public ObservableCollection<ExportJobViewModel> Jobs { get; } = [];

    /// <summary>True when there is nothing in the queue, for the empty state.</summary>
    public bool IsEmpty => Jobs.Count == 0;

    /// <summary>Stops a job through the command every surface uses.</summary>
    internal async Task CancelAsync(string jobId)
    {
        CommandResult result = await _session.ExecuteAsync(new CancelExportCommand(jobId)).ConfigureAwait(true);
        if (!result.Ok)
        {
            Status = result.Error ?? string.Empty;
        }
    }

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
            }
            else
            {
                existing.Update(update);
            }
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        int running = Jobs.Count(job => job.State == ExportJobState.Running);
        int waiting = Jobs.Count(job => job.State == ExportJobState.Queued);

        Status = _queue is null
            ? "There is no export queue in this window."
            : running + waiting == 0
                ? Jobs.Count == 0 ? "Nothing queued. Export from File, or press Export on a Quick Trim." : "Everything has finished."
                : string.Create(CultureInfo.InvariantCulture, $"{running} exporting, {waiting} waiting.");

        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>One job in the Export Queue panel.</summary>
public sealed partial class ExportJobViewModel : ObservableObject
{
    private readonly ExportQueuePanelViewModel _panel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private ExportJobState _state;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private string _stateText = string.Empty;

    internal ExportJobViewModel(ExportQueuePanelViewModel panel, ExportJobInfo job)
    {
        _panel = panel;
        Id = job.Id;
        OutputPath = job.OutputPath;
        Update(job);
    }

    /// <summary>The job id.</summary>
    public string Id { get; }

    /// <summary>The file it writes.</summary>
    public string OutputPath { get; }

    /// <summary>The file's name, which is what the row shows.</summary>
    public string Name => Path.GetFileName(OutputPath);

    /// <summary>True once it will not change again.</summary>
    public bool IsFinished => State is ExportJobState.Done or ExportJobState.Failed or ExportJobState.Cancelled;

    /// <summary>True for a failure, which the row shows in the warning colour.</summary>
    public bool IsFailed => State == ExportJobState.Failed;

    /// <summary>Takes a new report.</summary>
    internal void Update(ExportJobInfo job)
    {
        State = job.State;
        Progress = job.Progress;
        StateText = job.State switch
        {
            ExportJobState.Queued => "Waiting",
            ExportJobState.Running => string.Create(CultureInfo.InvariantCulture, $"{job.Progress * 100:F0}%"),
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
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private Task CancelAsync() => _panel.CancelAsync(Id);

    private bool CanCancel() => !IsFinished;

    [RelayCommand]
    private void ShowInFolder()
    {
        // Explorer with the file selected; if it is not there yet, the folder.
        string arguments = System.IO.File.Exists(OutputPath) ? $"/select,\"{OutputPath}\"" : $"\"{Path.GetDirectoryName(OutputPath)}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }
}
