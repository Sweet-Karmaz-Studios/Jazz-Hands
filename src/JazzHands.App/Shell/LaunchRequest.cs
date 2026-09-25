using System.IO;
using System.Text.Json.Nodes;

namespace JazzHands.App.Shell;

/// <summary>What a launch of JazzHands.exe asks for, besides a window.</summary>
public enum LaunchAction
{
    /// <summary>Just the editor: open a project if one was named, and show the window.</summary>
    Show,

    /// <summary>Start hidden in the notification area (<c>--background</c>, starting with Windows).</summary>
    Background,

    /// <summary>A new project (the jump list's New project).</summary>
    NewProject,

    /// <summary>Quick Trim a file, picked now when none was named.</summary>
    QuickTrim,

    /// <summary>Add files to the open project (Explorer's Add to the open project).</summary>
    AddMedia,

    /// <summary>Show the export queue.</summary>
    ExportQueue,

    /// <summary>Show a file in its folder (a finished export's notification).</summary>
    Reveal,
}

/// <summary>
/// A launch of JazzHands.exe: from the command line, a <c>.jazz</c> file, the jump list, an
/// Explorer menu, or a notification's button (a <c>jazzhands:</c> link). The first instance acts on
/// it itself; a second one hands it to the running editor over the control pipe as
/// <c>app.launch</c>, whatever its window is doing.
/// </summary>
/// <param name="Action">What to do.</param>
/// <param name="Files">The files it is about: a project, a recording, media, an export.</param>
public sealed record LaunchRequest(LaunchAction Action, IReadOnlyList<string> Files)
{
    /// <summary>The URL scheme notifications and the jump list use: <c>jazzhands:reveal?path=...</c>.</summary>
    public const string Scheme = "jazzhands";

    /// <summary>The project to open, for <see cref="LaunchAction.Show"/> and <see cref="LaunchAction.Background"/>.</summary>
    public string? Project => Action is LaunchAction.Show or LaunchAction.Background ? Files.FirstOrDefault() : null;

    /// <summary>
    /// Reads a command line: <c>[project.jazz]</c>, <c>--background</c>, <c>--new-project</c>,
    /// <c>--quick-trim [file]</c>, <c>--add-media file...</c>, <c>--export-queue</c>, or one
    /// <c>jazzhands:</c> link. Flags it does not know (<c>--new-instance</c>, <c>--spike</c>) are
    /// left for others.
    /// </summary>
    public static LaunchRequest Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.FirstOrDefault(arg => arg.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)) is { } link)
        {
            return FromLink(link);
        }

        string[] files = [.. args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).Select(Path.GetFullPath)];
        bool Has(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

        LaunchAction action = Has("--quick-trim") ? LaunchAction.QuickTrim
            : Has("--add-media") ? LaunchAction.AddMedia
            : Has("--new-project") ? LaunchAction.NewProject
            : Has("--export-queue") ? LaunchAction.ExportQueue
            : Has("--background") ? LaunchAction.Background
            : LaunchAction.Show;
        return new LaunchRequest(action, files);
    }

    /// <summary>
    /// A <c>jazzhands:</c> link: <c>jazzhands:show</c>, <c>jazzhands:exports</c>, or
    /// <c>jazzhands:reveal?path=C%3A%5Cout%5Ctrailer.mp4</c>. Anything else shows the window.
    /// </summary>
    public static LaunchRequest FromLink(string link)
    {
        ArgumentNullException.ThrowIfNull(link);
        string rest = link[(link.IndexOf(':', StringComparison.Ordinal) + 1)..].TrimStart('/');
        int question = rest.IndexOf('?', StringComparison.Ordinal);
        string verb = (question < 0 ? rest : rest[..question]).TrimEnd('/').ToLowerInvariant();
        string? path = question < 0
            ? null
            : rest[(question + 1)..].Split('&')
                .Select(pair => pair.Split('=', 2))
                .Where(pair => pair.Length == 2 && pair[0] == "path")
                .Select(pair => Uri.UnescapeDataString(pair[1]))
                .FirstOrDefault();

        return verb switch
        {
            "reveal" when path is not null => new LaunchRequest(LaunchAction.Reveal, [path]),
            "exports" => new LaunchRequest(LaunchAction.ExportQueue, []),
            _ => new LaunchRequest(LaunchAction.Show, []),
        };
    }

    /// <summary>A link that shows a file in its folder, for a notification's button.</summary>
    public static string RevealLink(string path) => $"{Scheme}:reveal?path={Uri.EscapeDataString(path)}";

    /// <summary>As the parameters of <c>app.launch</c>.</summary>
    public JsonObject ToJson() => new()
    {
        ["action"] = Action.ToString(),
        ["files"] = new JsonArray([.. Files.Select(file => (JsonNode?)file)]),
    };

    /// <summary>From the parameters of <c>app.launch</c>.</summary>
    public static LaunchRequest FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        LaunchAction action = Enum.TryParse(json["action"]?.GetValue<string>(), ignoreCase: true, out LaunchAction parsed) ? parsed : LaunchAction.Show;
        string[] files = json["files"] is JsonArray list ? [.. list.Select(file => file?.GetValue<string>()).OfType<string>()] : [];
        return new LaunchRequest(action, files);
    }
}
