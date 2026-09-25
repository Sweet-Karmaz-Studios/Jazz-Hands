using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using JazzHands.App.Services;
using JazzHands.App.ViewModels.Timeline;
using JazzHands.Control;
using JazzHands.Core.Commands;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Export;

namespace JazzHands.App.Shell;

/// <summary>
/// The status bar: the tool and snapping on the left; the playhead and the sequence's format in
/// the middle; exports, the cache, the GPU and remote clients on the right; and the bell.
/// </summary>
/// <remarks>
/// The view calls <see cref="Refresh"/> four times a second. The cache's size is read every ten
/// seconds, off the UI thread when there is a dispatcher to bring it back, since it asks a
/// database; the rest is cheap.
/// </remarks>
public sealed partial class StatusBarViewModel : ObservableObject
{
    private readonly ISession _session;
    private readonly Func<Flicks>? _playhead;
    private readonly Func<TimelineViewModel?> _timeline;
    private readonly IExportService? _exports;
    private readonly ControlServer? _server;
    private readonly IUiDispatcher? _ui;
    private int _ticks;
    private int _reading;

    [ObservableProperty]
    private string _tool = string.Empty;

    [ObservableProperty]
    private string _timecode = string.Empty;

    [ObservableProperty]
    private string _format = string.Empty;

    [ObservableProperty]
    private string _exportsText = string.Empty;

    [ObservableProperty]
    private string _cache = string.Empty;

    [ObservableProperty]
    private string _remote = string.Empty;

    /// <summary>A status bar over the editor's parts; any of them may be missing.</summary>
    public StatusBarViewModel(
        ISession session,
        NotificationService notifications,
        Func<TimelineViewModel?>? timeline = null,
        Func<Flicks>? playhead = null,
        IExportService? exports = null,
        ControlServer? server = null,
        string gpu = "",
        IUiDispatcher? ui = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(notifications);
        _session = session;
        _ui = ui;
        Notifications = notifications;
        _timeline = timeline ?? (() => null);
        _playhead = playhead;
        _exports = exports;
        _server = server;
        Gpu = gpu;
        Refresh();
    }

    /// <summary>The notifications, for the bell and the toasts.</summary>
    public NotificationService Notifications { get; }

    /// <summary>The GPU rendering, as Windows names it.</summary>
    public string Gpu { get; }

    /// <summary>Reads everything again.</summary>
    public void Refresh()
    {
        Project project = _session.Project;
        Sequence? sequence = project.ActiveSequence;
        ProjectSettings settings = sequence is null ? project.Settings : project.SettingsFor(sequence);

        Tool = _timeline() is { } timeline
            ? $"{TimelineTools.Describe(timeline.Tools.Tool)}, snapping {(timeline.Tools.Snapping ? "on" : "off")}"
            : string.Empty;
        Timecode = _playhead is null ? string.Empty : Core.Time.Timecode.Format(_playhead(), settings.FrameRate);
        Format = string.Create(CultureInfo.InvariantCulture, $"{settings.Width}x{settings.Height}, {FrameRate(settings.FrameRate)} fps");

        if (_exports is not null)
        {
            ExportJobInfo[] jobs = _exports.List();
            int running = jobs.Count(job => job.State == ExportJobState.Running);
            int waiting = jobs.Count(job => job.State is ExportJobState.Queued or ExportJobState.Paused);
            ExportsText = running + waiting == 0
                ? string.Empty
                : running > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"Exporting {running}{(waiting > 0 ? $", {waiting} waiting" : string.Empty)}, {jobs.Where(job => job.State == ExportJobState.Running).Average(job => job.Progress) * 100:0}%")
                    : string.Create(CultureInfo.InvariantCulture, $"{waiting} export(s) waiting");
        }

        if (_server is not null)
        {
            int clients = _server.Clients.Count;
            Remote = clients == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"{clients} remote client{(clients == 1 ? string.Empty : "s")}");
        }

        if (_ticks++ % 40 != 0)
        {
            return;
        }

        if (_ui is null)
        {
            Cache = ReadCache();
        }
        else if (Interlocked.Exchange(ref _reading, 1) == 0)
        {
            // Off the UI thread: the read asks the database. One at a time.
            _ = Task.Run(() =>
            {
                string cache = ReadCache();
                _ui.Post(() => Cache = cache);
                Volatile.Write(ref _reading, 0);
            });
        }
    }

    private string ReadCache()
    {
        try
        {
            CacheStatsInfo stats = _session.Query(new GetCacheStatsQuery());
            return string.Create(CultureInfo.InvariantCulture, $"Cache {ExportPresets.FormatBytes(stats.BlobBytes + stats.DatabaseBytes)}{(stats.CapBytes > 0 ? $" of {ExportPresets.FormatBytes(stats.CapBytes)}" : string.Empty)}");
        }
        catch (Exception error) when (error is CommandException or InvalidOperationException or System.IO.IOException)
        {
            return string.Empty;
        }
    }

    private static string FrameRate(Rational rate) =>
        rate.Den == 1 ? rate.Num.ToString(CultureInfo.InvariantCulture) : (rate.Num / (double)rate.Den).ToString("0.###", CultureInfo.InvariantCulture);
}
