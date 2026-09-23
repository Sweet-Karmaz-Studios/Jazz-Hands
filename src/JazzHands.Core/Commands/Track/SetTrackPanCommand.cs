namespace JazzHands.Core.Commands;

/// <summary>Sets an audio track's balance.</summary>
/// <remarks>Applied to the whole track after each clip's own pan: the side it moves away from is turned down.</remarks>
/// <param name="TrackId">Which track. It must be an audio track.</param>
/// <param name="Pan">-1 is hard left, 0 centre, 1 hard right.</param>
[Command("track.set-pan", Description = "Set an audio track's balance")]
public sealed record SetTrackPanCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Option("pan", "-1 hard left, 0 centre, 1 hard right")] double Pan) : ICommand;
