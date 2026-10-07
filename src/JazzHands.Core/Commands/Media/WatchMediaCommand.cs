using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Watches a folder and brings in each new recording once it has finished being written.</summary>
/// <remarks>
/// For a captures folder: OBS or ShadowPlay writes a file, and when it has stopped growing it is
/// imported, tagged with the watch's tags, the folder it is in and the day it was made. The watch
/// is kept with the project, so the editor, <c>jazz serve</c> or a headless MCP server watches it
/// again whenever the project is open; a one-off <c>jazz</c> process records it, and with
/// <c>--existing</c> imports what is there, and ends. Watching a folder again changes its tags and
/// bin.
/// </remarks>
/// <param name="Folder">The folder to watch, with its subfolders.</param>
/// <param name="Tags">Tags for everything it brings in.</param>
/// <param name="Bin">The bin folder they go in.</param>
/// <param name="Existing">Bring in what is already there too.</param>
[Command("media.watch", Description = "Watch a folder and bring in new recordings as they finish")]
public sealed record WatchMediaCommand(
    [property: Arg(0, "The folder to watch")] string Folder,
    [property: Option("tags", "Tags for everything it brings in")] EquatableArray<string> Tags = default,
    [property: Option("bin", "The bin folder they go in")] string Bin = "",
    [property: Option("existing", "Bring in what is already there too")] bool Existing = false) : ICommand;
