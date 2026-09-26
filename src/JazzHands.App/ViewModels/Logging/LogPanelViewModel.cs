using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Engine.Logging;
using Serilog.Events;

namespace JazzHands.App.ViewModels.Logging;

/// <summary>One line of the Log panel.</summary>
/// <param name="Entry">The log entry.</param>
public sealed record LogRow(LogEntry Entry)
{
    /// <summary>The time, as the list shows it.</summary>
    public string Time => Entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>The level, short.</summary>
    public string Level => Entry.Level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        _ => "FTL",
    };

    /// <summary>True for warnings and worse, which the list colours.</summary>
    public bool IsProblem => Entry.Level >= LogEventLevel.Warning;

    /// <summary>The line as text, for copying.</summary>
    public string Text => $"{Entry.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} [{Level}] {Entry.Source}: {Entry.Message}{(Entry.Exception is { } error ? Environment.NewLine + error : string.Empty)}";
}

/// <summary>
/// The Log panel: what the editor has logged, newest last, filtered by level and text, and
/// copied as text for a bug report.
/// </summary>
/// <remarks>
/// It reads the in-memory ring the logger keeps (the last 2000 entries), not the file, and hears
/// each new entry as it is written, gathered into one update of the list per burst.
/// </remarks>
public sealed partial class LogPanelViewModel : ToolViewModel
{
    /// <summary>The id the layout knows the panel by.</summary>
    public const string PanelId = "log";

    /// <summary>The most lines the list holds.</summary>
    public const int MaxRows = 2000;

    private readonly LogRingBufferSink? _ring;
    private readonly IUiDispatcher _ui;
    private readonly Action<string>? _copy;
    private readonly ConcurrentQueue<LogEntry> _incoming = new();
    private readonly List<LogEntry> _all = [];
    private int _flushQueued;

    [ObservableProperty]
    private LogEventLevel _level = LogEventLevel.Information;

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Creates the panel over the logger's ring.</summary>
    /// <param name="ring">The ring, or null when logging keeps none (the panel says so).</param>
    /// <param name="ui">The UI thread.</param>
    /// <param name="copy">Puts text on the clipboard.</param>
    public LogPanelViewModel(LogRingBufferSink? ring, IUiDispatcher ui, Action<string>? copy = null)
        : base(PanelId, "Log")
    {
        ArgumentNullException.ThrowIfNull(ui);
        _ring = ring;
        _ui = ui;
        _copy = copy;

        if (ring is not null)
        {
            _all.AddRange(ring.Snapshot());
            ring.EntryWritten += entry =>
            {
                _incoming.Enqueue(entry);
                if (Interlocked.Exchange(ref _flushQueued, 1) == 0)
                {
                    _ui.Post(Flush);
                }
            };
        }

        Filter();
    }

    /// <summary>The levels to filter by.</summary>
    public static IReadOnlyList<LogEventLevel> Levels { get; } = [LogEventLevel.Debug, LogEventLevel.Information, LogEventLevel.Warning, LogEventLevel.Error];

    /// <summary>The lines the filter lets through, oldest first.</summary>
    public ObservableCollection<LogRow> Rows { get; } = [];

    /// <summary>Copies the lines in view as text.</summary>
    [RelayCommand]
    public void Copy()
    {
        var text = new StringBuilder();
        foreach (LogRow row in Rows)
        {
            text.AppendLine(row.Text);
        }

        _copy?.Invoke(text.ToString());
        Status = string.Create(CultureInfo.InvariantCulture, $"Copied {Services.Words.Count(Rows.Count, "line")}.");
    }

    /// <summary>Empties the list and the ring.</summary>
    [RelayCommand]
    public void Clear()
    {
        _ring?.Clear();
        _all.Clear();
        Filter();
    }

    partial void OnLevelChanged(LogEventLevel value) => Filter();

    partial void OnSearchChanged(string value) => Filter();

    private void Flush()
    {
        Volatile.Write(ref _flushQueued, 0);
        while (_incoming.TryDequeue(out LogEntry? entry))
        {
            _all.Add(entry);
            if (Passes(entry))
            {
                Rows.Add(new LogRow(entry));
            }
        }

        Trim();
    }

    private void Filter()
    {
        Rows.Clear();
        foreach (LogEntry entry in _all.Where(Passes))
        {
            Rows.Add(new LogRow(entry));
        }

        Trim();
        Status = _ring is null ? "Logging keeps nothing in memory in this host." : string.Empty;
    }

    private void Trim()
    {
        while (_all.Count > MaxRows)
        {
            _all.RemoveAt(0);
        }

        while (Rows.Count > MaxRows)
        {
            Rows.RemoveAt(0);
        }
    }

    private bool Passes(LogEntry entry) =>
        entry.Level >= Level
        && (Search.Length == 0
            || entry.Message.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains(Search, StringComparison.OrdinalIgnoreCase));
}
