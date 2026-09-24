using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Closes the empty space on a track at a time, pulling everything after it back.</summary>
/// <param name="TrackId">The track with the gap.</param>
/// <param name="At">Any time inside the gap.</param>
[Command("clip.close-gap", Description = "Close a gap on a track, rippling every sync-locked track")]
public sealed record CloseGapCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Option("at", "A time inside the gap")] Flicks At) : ICommand;
