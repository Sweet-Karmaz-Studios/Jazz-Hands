using System.Globalization;
using System.IO;
using System.Windows.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using JazzHands.App.Services;
using JazzHands.Core.Export;
using JazzHands.Engine.Export;

namespace JazzHands.App.Shell;

/// <summary>What the badge over the taskbar button shows.</summary>
public enum DesktopBadge
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>An export or proxy is running.</summary>
    Exporting,

    /// <summary>The last export failed.</summary>
    Failed,

    /// <summary>A remote client, Claude Code or another, is attached.</summary>
    Attached,
}

/// <summary>
/// What Jazz Hands is doing, as the desktop shows it when nobody is looking at the window: the
/// notification area's tooltip and its export line, the taskbar button's progress and badge.
/// </summary>
/// <remarks>
/// Exports and proxies are both jobs on the export queue; a proxy is a job writing into the proxy
/// folder. A failed export turns the progress red and badges the button until the next job starts
/// or the person looks at the queue (<see cref="Acknowledge"/>). Claude Code attached through MCP
/// is the client <c>rpc:mcp</c>.
/// </remarks>
public sealed partial class DesktopStatus : ObservableObject
{
    private readonly IExportService? _exports;
    private readonly Func<string?> _proxyFolder;
    private readonly Func<IReadOnlyList<string>> _clients;
    private readonly IUiDispatcher _ui;
    private readonly HashSet<string> _failedSeen = [];
    private readonly HashSet<string> _finishedSeen = [];
    private IReadOnlyList<string> _lastClients = [];
    private int _proxyRun;
    private string? _failure;

    [ObservableProperty]
    private string _toolTip = "Jazz Hands";

    [ObservableProperty]
    private string _exportLine = string.Empty;

    [ObservableProperty]
    private string? _runningJobId;

    [ObservableProperty]
    private TaskbarItemProgressState _progressState = TaskbarItemProgressState.None;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private DesktopBadge _badge;

    [ObservableProperty]
    private bool _queuePaused;

    [ObservableProperty]
    private bool _clientAttached;

    /// <summary>A status over the queue and the control server; either may be missing.</summary>
    public DesktopStatus(IExportService? exports, IUiDispatcher ui, Func<string?>? proxyFolder = null, Func<IReadOnlyList<string>>? clients = null)
    {
        ArgumentNullException.ThrowIfNull(ui);
        _exports = exports;
        _ui = ui;
        _proxyFolder = proxyFolder ?? (() => null);
        _clients = clients ?? (() => []);

        // Jobs that had already finished or failed when the editor started are old news.
        foreach (ExportJobInfo job in exports?.List() ?? [])
        {
            if (job.IsFinished)
            {
                _finishedSeen.Add(job.Id);
            }

            if (job.State == ExportJobState.Failed)
            {
                _failedSeen.Add(job.Id);
            }
        }

        exports?.Changed += (_, _) => _ui.Post(Refresh);
        Refresh();
    }

    /// <summary>Raised when an export finishes, with the job, once; for the Windows notification.</summary>
    public event EventHandler<ExportJobInfo>? ExportFinished;

    /// <summary>Raised when the last proxy in a run of them is made, with how many were made.</summary>
    public event EventHandler<int>? ProxiesFinished;

    /// <summary>Raised when a client attaches, with its name, for the optional notification.</summary>
    public event EventHandler<string>? ClientArrived;

    /// <summary>The failure has been seen: the red and the badge go.</summary>
    public void Acknowledge()
    {
        _failure = null;
        Refresh();
    }

    /// <summary>Reads the queue and the clients again.</summary>
    public void Refresh()
    {
        ExportJobInfo[] jobs = _exports?.List() ?? [];
        string? proxies = _proxyFolder();
        bool IsProxy(ExportJobInfo job) =>
            proxies is not null && job.OutputPath.StartsWith(proxies.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        foreach (ExportJobInfo job in jobs)
        {
            if (job.State == ExportJobState.Failed && _failedSeen.Add(job.Id) && !IsProxy(job))
            {
                _failure = $"{Path.GetFileName(job.OutputPath)} failed: {job.Error}";
            }

            if (job.IsFinished && _finishedSeen.Add(job.Id) && !IsProxy(job) && job.Finished is not null)
            {
                ExportFinished?.Invoke(this, job);
            }
        }

        ExportJobInfo[] running = [.. jobs.Where(job => job.State == ExportJobState.Running)];
        ExportJobInfo[] exports = [.. running.Where(job => !IsProxy(job))];
        int proxiesMaking = jobs.Count(job => IsProxy(job) && !job.IsFinished);
        if (proxiesMaking > _proxyRun)
        {
            _proxyRun = proxiesMaking;
        }
        else if (proxiesMaking == 0 && _proxyRun > 0)
        {
            int made = _proxyRun;
            _proxyRun = 0;
            ProxiesFinished?.Invoke(this, made);
        }

        QueuePaused = jobs.Any(job => job.State == ExportJobState.Paused) && running.Length == 0;

        if (running.Length > 0)
        {
            // A new job running means the failure before it has been moved past.
            _failure = null;
        }

        ExportJobInfo? first = exports.FirstOrDefault();
        RunningJobId = first?.Id;
        ExportLine = first is null
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"Exporting {Path.GetFileName(first.OutputPath)}, {first.Progress * 100:0}%");

        IReadOnlyList<string> clients = _clients();
        bool claude = clients.Contains("rpc:mcp", StringComparer.Ordinal);
        ClientAttached = clients.Count > 0;
        foreach (string arrived in clients.Except(_lastClients, StringComparer.Ordinal))
        {
            ClientArrived?.Invoke(this, arrived);
        }

        _lastClients = [.. clients];

        var doing = new List<string>();
        if (first is not null)
        {
            doing.Add(exports.Length > 1
                ? string.Create(CultureInfo.InvariantCulture, $"exporting {exports.Length} files, {exports.Average(job => job.Progress) * 100:0}%")
                : string.Create(CultureInfo.InvariantCulture, $"exporting {first.Progress * 100:0}%"));
        }

        if (proxiesMaking > 0)
        {
            doing.Add(string.Create(CultureInfo.InvariantCulture, $"making {proxiesMaking} prox{(proxiesMaking == 1 ? "y" : "ies")}"));
        }

        if (QueuePaused && first is null)
        {
            doing.Add("exports paused");
        }

        if (_failure is not null)
        {
            doing.Add(_failure);
        }

        if (claude)
        {
            doing.Add("Claude attached");
        }
        else if (clients.Count > 0)
        {
            doing.Add(string.Create(CultureInfo.InvariantCulture, $"{clients.Count} remote client{(clients.Count == 1 ? string.Empty : "s")}"));
        }

        // The notification area's tooltip holds 127 characters.
        string tip = "Jazz Hands: " + (doing.Count == 0 ? "idle" : string.Join("; ", doing));
        ToolTip = tip.Length > 127 ? tip[..126] + "." : tip;

        ExportJobInfo[] active = [.. running];
        ProgressValue = active.Length == 0 ? 0 : active.Average(job => job.Progress);
        ProgressState = _failure is not null ? TaskbarItemProgressState.Error
            : active.Length > 0 ? TaskbarItemProgressState.Normal
            : QueuePaused ? TaskbarItemProgressState.Paused
            : TaskbarItemProgressState.None;
        if (_failure is not null && active.Length == 0)
        {
            ProgressValue = 1;
        }

        Badge = _failure is not null ? DesktopBadge.Failed
            : active.Length > 0 ? DesktopBadge.Exporting
            : claude || clients.Count > 0 ? DesktopBadge.Attached
            : DesktopBadge.None;
    }
}
