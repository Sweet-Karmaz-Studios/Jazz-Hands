using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Puts a copy of a clip somewhere else, leaving the original alone.</summary>
/// <param name="ClipId">Which clip to copy.</param>
/// <param name="At">Where the copy should start.</param>
/// <param name="ToTrackId">Which track to put it on. The same track when left out.</param>
/// <param name="NewClipId">The identifier for the copy. A fresh one when left out.</param>
[Command("clip.copy", Description = "Copy a clip to another time or track")]
public sealed record CopyClipCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("at", "Where the copy should start")] Flicks At,
    [property: Option("track", "Which track to put it on")] string? ToTrackId = null,
    [property: Option("id", "The identifier for the copy")] string? NewClipId = null) : ICommand;
