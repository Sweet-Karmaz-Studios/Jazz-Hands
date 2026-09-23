namespace JazzHands.Core.Commands;

/// <summary>Mutes or unmutes a track.</summary>
/// <param name="TrackId">Which track.</param>
/// <param name="Muted">True to mute it.</param>
[Command("track.set-mute", Description = "Mute or unmute a track")]
public sealed record SetTrackMuteCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Arg(1, "true to mute")] bool Muted) : ICommand;
