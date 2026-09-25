using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Watches a folder and brings in each new recording once it has finished being written.</summary>
/// <remarks>
/// For a captures folder: OBS or ShadowPlay writes a file, and when it has stopped growing it is
/// imported, tagged with the watch's tags, the folder it is in and the day it was made. The watch
/// belongs to the running editor or <c>jazz serve</c>, not to the project; a one-off <c>jazz</c>
/// process imports what is there with <c>--existing</c> and ends.
/// </remarks>
/// <param name="Folder">The folder to watch, with its subfolders.</param>
/// <param name="Tags">Tags for everything it brings in.</param>
/// <param name="Bin">The bin folder they go in.</param>
/// <param name="Existing">Bring in what is already there too.</param>
[Command("media.watch", Description = "Watch a folder and bring in new recordings as they finish",
    Undoable = false,
    NotUndoableReason = "It starts a watch; each file it brings in is its own undoable import.")]
public sealed record WatchMediaCommand(
    [property: Arg(0, "The folder to watch")] string Folder,
    [property: Option("tags", "Tags for everything it brings in")] EquatableArray<string> Tags = default,
    [property: Option("bin", "The bin folder they go in")] string Bin = "",
    [property: Option("existing", "Bring in what is already there too")] bool Existing = false) : ICommand;
