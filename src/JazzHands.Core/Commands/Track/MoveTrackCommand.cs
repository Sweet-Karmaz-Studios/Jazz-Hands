namespace JazzHands.Core.Commands;

/// <summary>Moves a track up or down the stack.</summary>
/// <remarks>
/// The other tracks close up behind it and open in front of it, so the orders stay contiguous and
/// a track dragged to position 0 really is at the bottom.
/// </remarks>
/// <param name="TrackId">Which track.</param>
/// <param name="ToOrder">Where it should end up, counting from the bottom.</param>
[Command("track.move", Description = "Move a track up or down the stack")]
public sealed record MoveTrackCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Arg(1, "Where it should end up, counting from the bottom")] int ToOrder) : ICommand;
