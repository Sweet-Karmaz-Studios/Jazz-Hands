namespace JazzHands.Core.Commands;

/// <summary>Sets how tall a track is drawn.</summary>
/// <param name="TrackId">Which track.</param>
/// <param name="Height">Height in device-independent pixels.</param>
[Command("track.set-height", Description = "Set how tall a track is drawn")]
public sealed record SetTrackHeightCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Arg(1, "Height in device-independent pixels")] double Height) : ICommand;
