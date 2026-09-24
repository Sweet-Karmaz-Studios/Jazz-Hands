using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Sets where an audio clip sits between left and right, or where it sits at one time.</summary>
/// <remarks>
/// A mono clip is panned with the constant power law, so it is as loud wherever it sits. A stereo
/// clip is balanced: the side it moves away from is turned down, and centre leaves both alone.
/// With a time it sets a keyframe there; without one a keyframed pan is refused.
/// </remarks>
/// <param name="ClipId">Which clip. It must be on an audio track.</param>
/// <param name="Pan">-1 is hard left, 0 centre, 1 hard right.</param>
/// <param name="At">Set a keyframe at this time on the sequence, inside the clip.</param>
[Command("audio.set-pan", Description = "Set an audio clip's pan, or a keyframe of it")]
public sealed record SetAudioPanCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("pan", "-1 hard left, 0 centre, 1 hard right")] double Pan,
    [property: Option("at", "Set a keyframe at this time on the sequence")] Flicks? At = null) : ICommand;
