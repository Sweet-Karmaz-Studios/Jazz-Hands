using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Sets a sequence's master volume, or its master volume at one time.</summary>
/// <remarks>
/// The master fader: after every track is summed and before the limiter, so turning it up drives
/// the limiter harder rather than past the ceiling. With a time it sets a keyframe there, as
/// <c>track.set-volume</c> does; without one a keyframed master is refused rather than flattened.
/// </remarks>
/// <param name="Db">Volume in decibels, from -144 (silence) to +24.</param>
/// <param name="At">Set a keyframe at this time on the sequence rather than the whole master.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
[Command("audio.set-master-volume", Description = "Set a sequence's master volume in dB, or a keyframe of it")]
public sealed record SetMasterVolumeCommand(
    [property: Option("db", "Volume in dB, -144 to 24")] double Db,
    [property: Option("at", "Set a keyframe at this time on the sequence")] Flicks? At = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : IMergeableCommand
{
    /// <inheritdoc />
    public bool Continues(ICommand previous) =>
        previous is SetMasterVolumeCommand before && before.SequenceId == SequenceId && before.At == At;
}
