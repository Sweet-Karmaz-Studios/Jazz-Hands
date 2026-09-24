using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Sets an audio clip's gain, or its gain at one time.</summary>
/// <remarks>
/// Without a time, a static gain in decibels for the whole clip. With one, a keyframe on the
/// clip's volume line at that time on the sequence: the first turns the line into a curve, which
/// is what Ctrl-clicking the rubber band on the timeline does. A clip whose gain already has
/// keyframes is refused without a time rather than flattened.
/// </remarks>
/// <param name="ClipId">Which clip. It must be on an audio track.</param>
/// <param name="Db">Gain in decibels, from -144 (silence) to +24. Zero leaves the level alone.</param>
/// <param name="At">Set a keyframe at this time on the sequence, inside the clip.</param>
[Command("audio.set-gain", Description = "Set an audio clip's gain in dB, or a keyframe of it")]
public sealed record SetAudioGainCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("db", "Gain in dB, -144 to 24")] double Db,
    [property: Option("at", "Set a keyframe at this time on the sequence")] Flicks? At = null) : ICommand;
