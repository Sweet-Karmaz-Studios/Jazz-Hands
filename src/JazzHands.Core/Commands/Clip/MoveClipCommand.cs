using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Moves a clip to another time, and optionally to another track.</summary>
/// <param name="ClipId">Which clip.</param>
/// <param name="To">Where it should start.</param>
/// <param name="ToTrackId">Which track to move it to. Stays where it is when left out.</param>
[Command("clip.move", Description = "Move a clip to another time or track")]
public sealed record MoveClipCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("to", "Where it should start")] Flicks To,
    [property: Option("track", "Which track to move it to")] string? ToTrackId = null) : ICommand;
