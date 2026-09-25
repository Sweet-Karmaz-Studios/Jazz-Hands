using System.IO;
using System.Windows.Shell;

namespace JazzHands.App.Shell;

/// <summary>
/// The jump list: right-click on the taskbar button or the Start menu entry. Recent projects, kept
/// in step with the recent list, and four tasks.
/// </summary>
/// <remarks>
/// A recent project is a <see cref="JumpPath"/>, which Windows opens through the <c>.jazz</c>
/// association; a task starts JazzHands.exe with a flag, and that launch hands itself to the
/// running editor (<see cref="LaunchRequest"/>). The log folder task opens Explorer there.
/// </remarks>
public static class JumpLists
{
    /// <summary>The category the recent projects are under.</summary>
    public const string RecentCategory = "Recent projects";

    /// <summary>The jump list for these recent projects.</summary>
    /// <param name="recent">Full paths, newest first; those not on disk are left out.</param>
    /// <param name="exe">JazzHands.exe.</param>
    /// <param name="logFolder">The log folder.</param>
    /// <param name="exists">Whether a file is there; for tests.</param>
    public static JumpList Build(IReadOnlyList<string> recent, string exe, string logFolder, Func<string, bool>? exists = null)
    {
        ArgumentNullException.ThrowIfNull(recent);
        exists ??= File.Exists;

        var list = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };
        foreach (string path in recent.Where(exists).Take(10))
        {
            list.JumpItems.Add(new JumpPath { Path = path, CustomCategory = RecentCategory });
        }

        list.JumpItems.Add(Task("New project", "Start an empty project", exe, "--new-project", exe));
        list.JumpItems.Add(Task("Quick Trim a file...", "Pick a recording and trim it", exe, "--quick-trim", exe));
        list.JumpItems.Add(Task("Open the export queue", "See what is exporting", exe, "--export-queue", exe));
        list.JumpItems.Add(Task("Open the log folder", "Where Jazz Hands writes its logs", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{logFolder}\"", exe));
        return list;
    }

    private static JumpTask Task(string title, string description, string application, string arguments, string icon) => new()
    {
        Title = title,
        Description = description,
        ApplicationPath = application,
        Arguments = arguments,
        IconResourcePath = icon,
        IconResourceIndex = 0,
    };
}
