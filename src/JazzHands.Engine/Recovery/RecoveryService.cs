using System.Globalization;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Engine.Commands;
using Serilog;

namespace JazzHands.Engine.Recovery;

/// <summary>What was found beside a project after it was opened.</summary>
/// <param name="ProjectPath">The .jazz file.</param>
/// <param name="RecoveryPath">The recovery copy autosave wrote, or empty when there is only the history.</param>
/// <param name="RecoveredAt">When the recovery copy was written, or when the project was last saved when there is none.</param>
/// <param name="CommandsAfterRecovery">Commands the history log holds that the copy does not.</param>
/// <param name="History">Every command the log holds since the project was last saved or opened.</param>
public sealed record RecoveryOffer(
    string ProjectPath,
    string RecoveryPath,
    DateTimeOffset RecoveredAt,
    IReadOnlyList<HistoryEntry> CommandsAfterRecovery,
    IReadOnlyList<HistoryEntry>? History = null)
{
    /// <summary>True when autosave left a copy; false when there is only the history to replay.</summary>
    public bool HasCopy => RecoveryPath.Length > 0;

    /// <summary>A sentence for the dialog and for the CLI.</summary>
    public string Describe()
    {
        string age = RecoveredAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        int edits = (History ?? CommandsAfterRecovery).Count;
        return HasCopy
            ? CommandsAfterRecovery.Count == 0
                ? $"Jazz Hands closed without saving. There is a recovery copy from {age}."
                : $"Jazz Hands closed without saving. There is a recovery copy from {age}, plus {CommandsAfterRecovery.Count} later commands."
            : $"Jazz Hands closed without saving. {edits} commands since it was saved at {age} can be replayed.";
    }
}

/// <summary>What replaying a recovery gave.</summary>
/// <param name="Project">The project as it was when the editor closed.</param>
/// <param name="From">What it was rebuilt from: "history" (the saved project and every command since) or "copy" (the recovery copy and the commands after it).</param>
/// <param name="Replayed">Commands replayed.</param>
/// <param name="Skipped">Commands that could not be replayed, each with why.</param>
public sealed record RecoveryResult(Project Project, string From, int Replayed, IReadOnlyList<string> Skipped);

/// <summary>An untitled project saved when the editor crashed, which has no folder of its own.</summary>
/// <param name="Path">The file.</param>
/// <param name="Name">The project's name.</param>
/// <param name="SavedAt">When it was written.</param>
public sealed record UntitledRecovery(string Path, string Name, DateTimeOffset SavedAt);

/// <summary>
/// Finds and applies what autosave and the history log left behind after a crash.
/// </summary>
/// <remarks>
/// Recovery never happens on its own. It is offered, and the user decides: the alternative is an
/// editor that opens a different project from the one that was asked for, which is worse than
/// losing the work because it looks like nothing went wrong.
///
/// The history log holds every command since the project was last saved or opened, with the
/// identifiers each one made (<see cref="IdScope"/>), so the saved project with the log replayed
/// over it is the edit exactly as it was, to the last command. The recovery copy is at most a
/// minute old; it is the fallback when the replay cannot go through (a command this version no
/// longer knows), with the commands after it replayed on top. Declining archives both into the
/// sidecar folder rather than deleting them: nothing is ever thrown away by a recovery prompt.
/// </remarks>
public sealed class RecoveryService
{
    /// <summary>Moves where untitled projects are rescued to, for tests.</summary>
    public const string FolderVariable = "JAZZ_RECOVERY_DIR";

    private const int ArchivesKept = 5;

    private readonly ILogger _log = Log.ForContext<RecoveryService>();

    /// <summary>Where a crash saves a project that was never saved: %LOCALAPPDATA%\JazzHands\recovery.</summary>
    public static string UntitledFolder =>
        Environment.GetEnvironmentVariable(FolderVariable) is { Length: > 0 } folder
            ? folder
            : Path.Combine(JazzHands.Core.JazzFolders.Local, "recovery");

    /// <summary>True when a crash left a recovery copy or a history beside a project, whether or not they are worth offering.</summary>
    public static bool HasLeftovers(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        string full = Path.GetFullPath(projectPath);
        return File.Exists(ProjectPaths.RecoveryFile(full))
            || (File.Exists(ProjectPaths.HistoryFile(full)) && new FileInfo(ProjectPaths.HistoryFile(full)).Length > 0);
    }

    /// <summary>Looks for work to recover beside a project, returning null when there is none.</summary>
    public RecoveryOffer? Find(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        string full = Path.GetFullPath(projectPath);
        string recovery = ProjectPaths.RecoveryFile(full);
        DateTimeOffset saved = File.Exists(full) ? File.GetLastWriteTimeUtc(full) : DateTimeOffset.MinValue;

        // The log is cleared by every save and open, so what it holds is the work since the file
        // was written. A log older than the project is from a session whose work was saved.
        IReadOnlyList<HistoryEntry> history = [.. HistoryLog.Read(full).Where(entry => entry.Timestamp > saved)];

        if (File.Exists(recovery))
        {
            DateTimeOffset written = File.GetLastWriteTimeUtc(recovery);

            // A recovery copy older than the project itself is left over from a session whose
            // work was saved. Offering it would invite the user to undo their own save.
            if (written > saved)
            {
                return new RecoveryOffer(full, recovery, written, [.. history.Where(entry => entry.Timestamp > written)], history);
            }

            _log.Debug("Ignoring a recovery copy older than {Project}", full);
        }

        return history.Count > 0 && File.Exists(full)
            ? new RecoveryOffer(full, string.Empty, saved, history, history)
            : null;
    }

    /// <summary>Untitled projects a crash saved, newest first.</summary>
    public IReadOnlyList<UntitledRecovery> FindUntitled()
    {
        string folder = UntitledFolder;
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var found = new List<UntitledRecovery>();
        foreach (string file in Directory.EnumerateFiles(folder, "*.jazz"))
        {
            try
            {
                ProjectLoad load = ProjectFile.Load(file);
                found.Add(new UntitledRecovery(file, load.Project.Name, File.GetLastWriteTimeUtc(file)));
            }
            catch (Exception error) when (error is ProjectFileException or IOException or UnauthorizedAccessException)
            {
                _log.Warning(error, "Skipping an unreadable rescued project {Path}", file);
            }
        }

        return [.. found.OrderByDescending(item => item.SavedAt)];
    }

    /// <summary>
    /// Opens the recovery copy as the project, without touching the project file.
    /// </summary>
    /// <remarks>
    /// The copy is parsed as though it were at the project's own path, so media paths inside it
    /// still resolve. Nothing is replayed; <see cref="Replay"/> is what recovers the last minute.
    /// </remarks>
    public ProjectLoad Accept(RecoveryOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);

        string text = File.ReadAllText(offer.HasCopy ? offer.RecoveryPath : offer.ProjectPath);
        _log.Information("Recovered {Project} from {Copy}", offer.ProjectPath, offer.RecoveryPath);

        return ProjectFile.Parse(text, offer.ProjectPath);
    }

    /// <summary>
    /// Rebuilds the project as it was when the editor closed: the saved project with every command
    /// since replayed, or when that cannot go through, the recovery copy with the commands after it.
    /// </summary>
    /// <param name="offer">What <see cref="Find"/> found.</param>
    /// <param name="services">Where the command handlers come from.</param>
    public RecoveryResult Replay(RecoveryOffer offer, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(services);

        IReadOnlyList<HistoryEntry> history = offer.History ?? offer.CommandsAfterRecovery;
        if (history.Count > 0 && File.Exists(offer.ProjectPath))
        {
            Project saved = ProjectFile.Load(offer.ProjectPath).Project;
            (Project replayed, int count, List<string> skipped) = Run(saved, offer.ProjectPath, history, services);
            if (skipped.Count == 0 || !offer.HasCopy)
            {
                _log.Information("Recovered {Project} by replaying {Count} commands over the saved file", offer.ProjectPath, count);
                return new RecoveryResult(replayed, "history", count, skipped);
            }

            _log.Warning("Replaying the history of {Project} skipped {Skipped} commands; falling back on the recovery copy", offer.ProjectPath, skipped.Count);
        }

        Project copy = Accept(offer).Project;
        (Project result, int replayedAfter, List<string> skippedAfter) = Run(copy, offer.ProjectPath, offer.CommandsAfterRecovery, services);
        return new RecoveryResult(result, "copy", replayedAfter, skippedAfter);
    }

    /// <summary>
    /// Moves the recovery copy and the history into the sidecar's <c>recovered</c> folder, which
    /// is what declining means, and what starting to edit without recovering does. The newest few
    /// are kept.
    /// </summary>
    public void Discard(RecoveryOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        Archive(offer.ProjectPath);
    }

    /// <summary>Archives whatever a crash left beside a project; see <see cref="Discard"/>.</summary>
    public void Archive(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        string full = Path.GetFullPath(projectPath);
        string recovery = ProjectPaths.RecoveryFile(full);
        string history = ProjectPaths.HistoryFile(full);
        if (!File.Exists(recovery) && !File.Exists(history))
        {
            return;
        }

        string archives = Path.Combine(ProjectPaths.SidecarFolder(full), "recovered");
        string folder = Path.Combine(archives, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(folder);
            Move(recovery, Path.Combine(folder, "recovery.jazz"));
            Move(history, Path.Combine(folder, "history.jsonl"));

            foreach (string old in Directory.GetDirectories(archives).Order(StringComparer.Ordinal).SkipLast(ArchivesKept))
            {
                Directory.Delete(old, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.Warning(error, "Could not archive the recovery files of {Project}", full);
        }
    }

    /// <summary>Removes a rescued untitled project, once it has been recovered or declined.</summary>
    public void DiscardUntitled(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
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

    /// <summary>Writes a project that was never saved where <see cref="FindUntitled"/> looks.</summary>
    public static string RescueUntitled(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        Directory.CreateDirectory(UntitledFolder);
        string name = string.Concat(project.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        string path = Path.Combine(UntitledFolder, $"{(name.Length == 0 ? "Untitled" : name)}-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.jazz");

        // As it is: a project that was never saved has absolute media paths, and relative ones
        // would resolve against this folder.
        ProjectFile.WriteAtomic(path, ProjectFile.Render(project));
        return path;
    }

    private static void Move(string from, string to)
    {
        if (File.Exists(from))
        {
            File.Move(from, to, overwrite: true);
        }
    }

    /// <summary>Replays commands over a project in a dispatcher of its own, with the identifiers they made.</summary>
    private (Project Project, int Replayed, List<string> Skipped) Run(Project start, string projectPath, IReadOnlyList<HistoryEntry> entries, IServiceProvider services)
    {
        var skipped = new List<string>();
        int replayed = 0;
        Core.Time.Rational rate = start.SettingsFor(start.ActiveSequence ?? start.Sequences[0]).FrameRate;

        CommandDispatcher dispatcher = new(start, services, undoLimit: int.MaxValue) { ProjectPath = projectPath };
        try
        {
            foreach (HistoryEntry entry in entries)
            {
                ICommand command;
                try
                {
                    command = (ICommand)CommandRegistry.FromJson(entry.Name, entry.Arguments, rate);
                }
                catch (Exception error) when (error is CommandException or ArgumentException or InvalidOperationException or InvalidCastException or System.Text.Json.JsonException)
                {
                    skipped.Add($"{entry.Name}: {error.Message}");
                    continue;
                }

                CommandResult result = dispatcher.ExecuteAsync(command, "recovery", IdScope.Replaying(entry.Ids ?? [])).GetAwaiter().GetResult();
                if (result.Ok)
                {
                    replayed++;
                }
                else
                {
                    skipped.Add($"{entry.Name}: {result.Error}");
                }
            }

            return (dispatcher.Project, replayed, skipped);
        }
        finally
        {
            dispatcher.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
