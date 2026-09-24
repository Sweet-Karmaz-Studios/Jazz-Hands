using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Sets an audio track's volume, or its volume at one time.</summary>
/// <remarks>
/// The track fader: applied to everything on the track after each clip's own gain. With a time,
/// it sets a keyframe there (the first one turns automation on), which is what moving a fader
/// while writing automation does. Without one it sets the whole track, and a track whose volume
/// has keyframes is refused rather than flattened: give a time, or clear the keyframes first.
/// </remarks>
/// <param name="TrackId">Which track. It must be an audio track.</param>
/// <param name="Db">Volume in decibels, from -144 (silence) to +24.</param>
/// <param name="At">Set a keyframe at this time on the sequence rather than the whole track.</param>
[Command("track.set-volume", Description = "Set an audio track's volume in dB, or a keyframe of it")]
public sealed record SetTrackVolumeCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Option("db", "Volume in dB, -144 to 24")] double Db,
    [property: Option("at", "Set a keyframe at this time on the sequence")] Flicks? At = null) : ICommand;
