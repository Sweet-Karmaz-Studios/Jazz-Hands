namespace JazzHands.Core.Commands;

/// <summary>Sets an audio track's volume.</summary>
/// <remarks>The track fader: applied to everything on the track after each clip's own gain.</remarks>
/// <param name="TrackId">Which track. It must be an audio track.</param>
/// <param name="Db">Volume in decibels, from -144 (silence) to +24.</param>
[Command("track.set-volume", Description = "Set an audio track's volume in dB")]
public sealed record SetTrackVolumeCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Option("db", "Volume in dB, -144 to 24")] double Db) : ICommand;
