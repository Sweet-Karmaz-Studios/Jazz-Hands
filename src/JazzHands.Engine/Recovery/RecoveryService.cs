using JazzHands.Core.Serialization;
using Serilog;

namespace JazzHands.Engine.Recovery;

/// <summary>What was found beside a project after it was opened.</summary>
/// <param name="ProjectPath">The .jazz file.</param>
/// <param name="RecoveryPath">The recovery copy, when there is one.</param>
/// <param name="RecoveredAt">When the recovery copy was written.</param>
/// <param name="CommandsAfterRecovery">Commands the history log holds that the copy does not.</param>
public sealed record RecoveryOffer(
    string ProjectPath,
    string RecoveryPath,
    DateTimeOffset RecoveredAt,
    IReadOnlyList<HistoryEntry> CommandsAfterRecovery)
{
    /// <summary>A sentence for the dialog and for the CLI.</summary>
    public string Describe()
    {
        string age = $"from {RecoveredAt.ToLocalTime():HH:mm:ss}";
        return CommandsAfterRecovery.Count == 0
            ? $"Jazz Hands closed without saving. There is a recovery copy {age}."
            : $"Jazz Hands closed without saving. There is a recovery copy {age}, plus {CommandsAfterRecovery.Count} later commands.";
    }
}

/// <summary>
/// Finds and applies what autosave left behind after a crash.
/// </summary>
/// <remarks>
/// Recovery never happens on its own. A recovery copy is offered, and the user decides: the
/// alternative is an editor that opens a different project from the one that was asked for, which
/// is worse than losing the work because it looks like nothing went wrong.
///
/// What is on offer is the recovery copy plus the commands the history log recorded after it was
/// written. Replaying those is a Phase 06 job, once commands exist to replay; until then the
/// service reports how many there are so nothing is lost silently.
/// </remarks>
public sealed class RecoveryService
{
    private readonly ILogger _log = Log.ForContext<RecoveryService>();

    /// <summary>Looks for a recovery copy beside a project, returning null when there is none.</summary>
    public RecoveryOffer? Find(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        string full = Path.GetFullPath(projectPath);
        string recovery = ProjectPaths.RecoveryFile(full);

        if (!File.Exists(recovery))
        {
            return null;
        }

        DateTimeOffset written = File.GetLastWriteTimeUtc(recovery);

        // A recovery copy older than the project itself is left over from a session whose work
        // was saved. Offering it would invite the user to undo their own save.
        if (File.Exists(full) && File.GetLastWriteTimeUtc(full) >= written)
        {
            _log.Debug("Ignoring a recovery copy older than {Project}", full);
            return null;
        }

        IReadOnlyList<HistoryEntry> later =
        [
            .. HistoryLog.Read(full).Where(entry => entry.Timestamp > written),
        ];

        return new RecoveryOffer(full, recovery, written, later);
    }

    /// <summary>
    /// Opens the recovery copy as the project, without touching the project file.
    /// </summary>
    /// <remarks>
    /// The copy is parsed as though it were at the project's own path, so media paths inside it
    /// still resolve. The project file is left exactly as it was until the user saves, which is
    /// what makes accepting a recovery safe to change their mind about.
    /// </remarks>
    public ProjectLoad Accept(RecoveryOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);

        string text = File.ReadAllText(offer.RecoveryPath);
        _log.Information("Recovered {Project} from {Copy}", offer.ProjectPath, offer.RecoveryPath);

        return ProjectFile.Parse(text, offer.ProjectPath);
    }

    /// <summary>Throws the recovery copy and the history away, which is what declining means.</summary>
    public void Discard(RecoveryOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);

        Delete(offer.RecoveryPath);
        Delete(ProjectPaths.HistoryFile(offer.ProjectPath));
    }

    private void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.Warning(error, "Could not remove {Path}", path);
        }
    }
}
