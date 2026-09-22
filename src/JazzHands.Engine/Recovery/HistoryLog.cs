using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JazzHands.Core.Serialization;
using Serilog;

namespace JazzHands.Engine.Recovery;

/// <summary>One command as the history log recorded it.</summary>
/// <param name="Timestamp">When it ran, in UTC.</param>
/// <param name="Name">The command's registered name, for example clip.split.</param>
/// <param name="Arguments">Its arguments, exactly as they were dispatched.</param>
public sealed record HistoryEntry(DateTimeOffset Timestamp, string Name, JsonObject Arguments);

/// <summary>
/// The append-only record of every command that ran since the last save.
/// </summary>
/// <remarks>
/// One JSON object per line, in <c>&lt;project&gt;.jazz.d/history.jsonl</c>. Line-delimited and
/// flushed per command on purpose: a crash truncates at most the line being written, and every
/// line before it is still readable, which is exactly what recovery needs. A single JSON array
/// would be unreadable after a crash because it would never be closed.
///
/// The log is what makes recovery better than autosave alone. Autosave is at most sixty seconds
/// old; the log covers the work done since.
/// </remarks>
public sealed class HistoryLog : IDisposable
{
    private readonly ILogger _log = Log.ForContext<HistoryLog>();
    private readonly StreamWriter? _writer;
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Opens the log for a project, creating the sidecar folder.</summary>
    public HistoryLog(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        Path = ProjectPaths.HistoryFile(projectPath);
        ProjectPaths.EnsureSidecar(projectPath);

        try
        {
            _writer = new StreamWriter(
                new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
        }
        catch (IOException error)
        {
            // A read-only folder or a second instance holding the file must not stop editing.
            // Losing the ability to replay is worse than nothing but much better than refusing
            // to open the project.
            _log.Warning(error, "Command history is not being recorded for {Project}", projectPath);
        }
    }

    /// <summary>Where the log lives.</summary>
    public string Path { get; }

    /// <summary>True when commands are actually being written down.</summary>
    public bool IsRecording => _writer is not null;

    /// <summary>Reads a log, skipping any line a crash left half-written.</summary>
    public static IReadOnlyList<HistoryEntry> Read(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        string path = ProjectPaths.HistoryFile(projectPath);
        if (!File.Exists(path))
        {
            return [];
        }

        var entries = new List<HistoryEntry>();

        // Shared for writing, because the log is usually still open: the editor reads its own
        // history to show what would be replayed, and a second instance may be appending to it.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            HistoryEntry? entry = TryParse(line);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>Appends one command.</summary>
    public void Append(string name, JsonObject arguments, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(arguments);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_writer is null)
        {
            return;
        }

        var entry = new JsonObject
        {
            ["ts"] = (clock ?? TimeProvider.System).GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            ["command"] = name,
            ["args"] = arguments.DeepClone(),
        };

        lock (_gate)
        {
            _writer.WriteLine(entry.ToJsonString());
        }
    }

    /// <summary>Empties the log, which is what saving the project does.</summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_writer is null)
        {
            return;
        }

        lock (_gate)
        {
            _writer.Flush();
            _writer.BaseStream.SetLength(0);

            // Truncating does not move the write position, so without this the next command
            // would land at the old end of the file behind a run of zero bytes.
            _writer.BaseStream.Position = 0;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer?.Dispose();
    }

    /// <summary>
    /// Parses one line, returning null rather than throwing for a line that is not whole.
    /// </summary>
    /// <remarks>
    /// The last line of a log that was being written when the process died is usually truncated.
    /// That is expected, not an error: everything before it is still good.
    /// </remarks>
    private static HistoryEntry? TryParse(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject entry)
            {
                return null;
            }

            string? name = entry["command"]?.GetValue<string>();
            if (name is null)
            {
                return null;
            }

            DateTimeOffset timestamp = entry["ts"]?.GetValue<string>() is { } text
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, out DateTimeOffset parsed)
                    ? parsed
                    : default;

            return new HistoryEntry(timestamp, name, entry["args"] as JsonObject ?? []);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
