using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Makes a subclip: a media item for a stretch of another's file.</summary>
/// <remarks>
/// The subclip sits in the Media panel like any other item and plays the same file; put on a
/// timeline, a clip of it starts at its in and runs to its out. Trimming can still reach the rest
/// of the file.
/// </remarks>
/// <param name="MediaId">The media item whose file it is a stretch of.</param>
/// <param name="In">Where it starts in the file.</param>
/// <param name="Out">Where it ends in the file.</param>
/// <param name="Name">Its name; the file's name and its in point when left out.</param>
/// <param name="Folder">The Media panel folder to put it in; the file's own when left out.</param>
/// <param name="NewMediaId">The identifier to give it. A fresh one when left out.</param>
[Command("media.add-subclip", Description = "Make a media item for a stretch of another's file")]
public sealed record AddSubclipCommand(
    [property: Arg(0, "The media id")] string MediaId,
    [property: Option("in", "Where it starts in the file")] Flicks In,
    [property: Option("out", "Where it ends in the file")] Flicks Out,
    [property: Option("name", "Its name")] string? Name = null,
    [property: Option("folder", "The Media panel folder to put it in")] string? Folder = null,
    [property: Option("id", "The identifier to give it")] string? NewMediaId = null) : ICommand;
