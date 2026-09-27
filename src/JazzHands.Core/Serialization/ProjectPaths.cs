namespace JazzHands.Core.Serialization;

/// <summary>
/// Where a project keeps its things, and how paths inside it are spelled.
/// </summary>
/// <remarks>
/// A project is one file plus a sidecar folder: <c>trailer.jazz</c> and <c>trailer.jazz.d/</c>.
/// The file is the only thing that has to be backed up or committed. The folder holds the recovery copy,
/// the command history, recoveries set aside and motion analyses, none of which needs keeping,
/// and all of which belong in <c>.gitignore</c>. Thumbnails, waveforms and proxies are not here:
/// they are in the per-user cache, keyed by each file's hash, so a moved project keeps them.
///
/// Media paths inside the file are relative to the project file and use forward slashes, so a
/// project copied to another machine or another drive letter still finds its footage. An absolute
/// path is accepted, because a hand editor will write one, and is converted on the next save when
/// the file and the media sit on the same volume.
/// </remarks>
public static class ProjectPaths
{
    /// <summary>The extension a project file has.</summary>
    public const string Extension = ".jazz";

    /// <summary>The sidecar folder for a project file.</summary>
    public static string SidecarFolder(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        return Path.GetFullPath(projectPath) + ".d";
    }

    /// <summary>The recovery copy written by autosave.</summary>
    public static string RecoveryFile(string projectPath) =>
        Path.Combine(SidecarFolder(projectPath), "recovery.jazz");

    /// <summary>The append-only command log used to replay after a crash.</summary>
    public static string HistoryFile(string projectPath) =>
        Path.Combine(SidecarFolder(projectPath), "history.jsonl");

    /// <summary>Creates the sidecar folder if it is not there, and returns it.</summary>
    public static string EnsureSidecar(string projectPath)
    {
        string folder = SidecarFolder(projectPath);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Turns a stored media path into a full path on this machine.
    /// </summary>
    /// <param name="projectPath">The .jazz file the path is relative to; empty for a project never saved, whose paths are full ones.</param>
    /// <param name="storedPath">The path as the file holds it.</param>
    public static string Resolve(string projectPath, string storedPath)
    {
        ArgumentNullException.ThrowIfNull(storedPath);

        // A project never saved has no folder to be relative to (dragging a clip out of an
        // untitled project's Media panel crashed on this, 2026-09-27).
        if (Path.IsPathRooted(storedPath) || string.IsNullOrWhiteSpace(projectPath))
        {
            return Path.GetFullPath(storedPath);
        }

        string folder = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
        return Path.GetFullPath(Path.Combine(folder, storedPath));
    }

    /// <summary>
    /// Turns a path on this machine into the form the file stores.
    /// </summary>
    /// <remarks>
    /// Relative with forward slashes when the media and the project share a volume, absolute
    /// otherwise. Footage on a second drive is common enough that refusing it would be worse than
    /// a project that only opens on this machine, so it is kept and the validator warns.
    /// </remarks>
    public static string Store(string projectPath, string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);

        if (!Path.IsPathRooted(mediaPath))
        {
            return Normalize(mediaPath);
        }

        string folder = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
        string full = Path.GetFullPath(mediaPath);

        if (!string.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
        {
            return Normalize(full);
        }

        return Normalize(Path.GetRelativePath(folder, full));
    }

    /// <summary>True when a stored path will only ever work on the machine that wrote it.</summary>
    public static bool IsMachineSpecific(string storedPath) =>
        Path.IsPathRooted(storedPath ?? throw new ArgumentNullException(nameof(storedPath)));

    /// <summary>Forward slashes, so the same project text is produced on any machine.</summary>
    private static string Normalize(string path) => path.Replace('\\', '/');
}
