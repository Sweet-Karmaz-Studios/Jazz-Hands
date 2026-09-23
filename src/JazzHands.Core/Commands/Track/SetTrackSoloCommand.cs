namespace JazzHands.Core.Commands;

/// <summary>Solos or unsolos a track.</summary>
/// <remarks>While any track is soloed, only soloed tracks are heard.</remarks>
/// <param name="TrackId">Which track.</param>
/// <param name="Solo">True to solo it.</param>
[Command("track.set-solo", Description = "Solo or unsolo a track")]
public sealed record SetTrackSoloCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Arg(1, "true to solo")] bool Solo) : ICommand;
