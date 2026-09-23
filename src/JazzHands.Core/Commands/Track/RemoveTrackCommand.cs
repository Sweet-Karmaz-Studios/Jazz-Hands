namespace JazzHands.Core.Commands;

/// <summary>Removes a track and everything on it.</summary>
/// <param name="TrackId">Which track.</param>
[Command("track.remove", Description = "Remove a track and its clips")]
public sealed record RemoveTrackCommand(
    [property: Arg(0, "The track id")] string TrackId) : ICommand;
