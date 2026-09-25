using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Sets an audio track's balance, or its balance at one time.</summary>
/// <remarks>
/// Applied to the whole track after each clip's own pan: the side it moves away from is turned
/// down. With a time it sets a keyframe there; without one a keyframed balance is refused, as
/// <c>track.set-volume</c> refuses a keyframed volume.
/// </remarks>
/// <param name="TrackId">Which track. It must be an audio track.</param>
/// <param name="Pan">-1 is hard left, 0 centre, 1 hard right.</param>
/// <param name="At">Set a keyframe at this time on the sequence rather than the whole track.</param>
[Command("track.set-pan", Description = "Set an audio track's balance, or a keyframe of it")]
public sealed record SetTrackPanCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Option("pan", "-1 hard left, 0 centre, 1 hard right")] double Pan,
    [property: Option("at", "Set a keyframe at this time on the sequence")] Flicks? At = null) : IMergeableCommand
{
    /// <inheritdoc />
    public bool Continues(ICommand previous) =>
        previous is SetTrackPanCommand before && before.TrackId == TrackId && before.At == At;
}
