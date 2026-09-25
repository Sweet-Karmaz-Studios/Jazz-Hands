using System.Globalization;
using System.Text;
using JazzHands.Engine.Commands;
using Serilog;

namespace JazzHands.Engine.Recovery;

/// <summary>
/// What is written when the editor meets an error it cannot carry on from: the error, the
/// project, what was rescued, and the last fifty commands, so the crash can be reproduced by
/// replaying them (Phase 33).
/// </summary>
public static class CrashReport
{
    /// <summary>
    /// Rescues the session's unsaved work, writes the report beside the logs, and logs it. Never
    /// throws: it runs while the process is going down.
    /// </summary>
    /// <param name="error">What went wrong.</param>
    /// <param name="session">The session, when there is one.</param>
    /// <param name="folder">Where the report goes; the log folder by default.</param>
    /// <returns>The report's path and where the work was rescued to, either null when it could not be written.</returns>
    public static (string? Report, string? Rescued) Write(Exception error, Session? session, string? folder = null)
    {
        string? rescued = session?.Rescue();
        string? path = null;
        IReadOnlyList<RecentCommand> recent = session?.RecentCommands ?? [];

        try
        {
            folder ??= Logging.LogSetup.DefaultLogDirectory;
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, $"crash-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.txt");
            File.WriteAllText(path, Describe(error, session, rescued, recent), new UTF8Encoding(false));
        }
        catch (Exception writing) when (writing is IOException or UnauthorizedAccessException)
        {
            path = null;
        }

        Log.ForContext(typeof(CrashReport)).Fatal(
            error,
            "Crashed with {Project} open; unsaved work {Rescued}; report {Report}; last commands {Commands}",
            session?.ProjectPath ?? string.Empty,
            rescued ?? "(nothing to save)",
            path ?? "(not written)",
            recent.Select(command => $"{command.Name} {command.Arguments}").ToArray());

        return (path, rescued);
    }

    /// <summary>The report's text.</summary>
    public static string Describe(Exception error, Session? session, string? rescued, IReadOnlyList<RecentCommand> recent)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(recent);

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Jazz Hands crash report, {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Version: {typeof(CrashReport).Assembly.GetName().Version}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Project: {(session is null ? "(no session)" : session.ProjectPath.Length == 0 ? $"{session.Project.Name} (never saved)" : session.ProjectPath)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Unsaved work saved to: {rescued ?? "(nothing unsaved, or it could not be written)"}");
        text.AppendLine();
        text.AppendLine(error.ToString());
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"The last {recent.Count} commands, oldest first (a jazz apply script of the ones that worked reproduces the edit):");
        foreach (RecentCommand command in recent)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{command.At:HH:mm:ss.fff}  {(command.Ok ? "ok    " : $"failed {command.Code}")}  {command.Issuer,-8} {command.Name} {command.Arguments}");
        }

        return text.ToString();
    }
}
