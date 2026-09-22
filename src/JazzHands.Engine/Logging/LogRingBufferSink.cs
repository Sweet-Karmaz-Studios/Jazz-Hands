using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace JazzHands.Engine.Logging;

/// <summary>One rendered log line, as the Log panel and the control API event stream see it.</summary>
/// <param name="Timestamp">When the entry was written.</param>
/// <param name="Level">Its severity.</param>
/// <param name="Source">The SourceContext, shortened to a type name.</param>
/// <param name="Message">The rendered message.</param>
/// <param name="Exception">The exception text, when there was one.</param>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogEventLevel Level,
    string Source,
    string Message,
    string? Exception);

/// <summary>
/// Keeps the most recent log entries in memory so the Log panel and "jazz rpc subscribe log" can
/// show them without reading the file back. Bounded, so a runaway logger cannot eat the heap.
/// </summary>
public sealed class LogRingBufferSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly int _capacity;

    /// <summary>Creates a sink holding at most <paramref name="capacity"/> entries.</summary>
    public LogRingBufferSink(int capacity = 2000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>Raised on the logging thread for every entry. Handlers must not block.</summary>
    public event Action<LogEntry>? EntryWritten;

    /// <summary>The buffered entries, oldest first.</summary>
    public IReadOnlyList<LogEntry> Snapshot() => [.. _entries];

    /// <summary>The most recent entries, oldest first, at most <paramref name="count"/> of them.</summary>
    public IReadOnlyList<LogEntry> Recent(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        LogEntry[] all = [.. _entries];
        return count >= all.Length ? all : all[^count..];
    }

    /// <summary>Drops every buffered entry.</summary>
    public void Clear() => _entries.Clear();

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var entry = new LogEntry(
            logEvent.Timestamp,
            logEvent.Level,
            ShortenSource(logEvent),
            logEvent.RenderMessage(),
            logEvent.Exception?.ToString());

        _entries.Enqueue(entry);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
            // Trim to capacity.
        }

        EntryWritten?.Invoke(entry);
    }

    private static string ShortenSource(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? value))
        {
            return string.Empty;
        }

        string text = value is ScalarValue { Value: string scalar } ? scalar : value.ToString().Trim('"');
        int lastDot = text.LastIndexOf('.');
        return lastDot >= 0 && lastDot < text.Length - 1 ? text[(lastDot + 1)..] : text;
    }
}
