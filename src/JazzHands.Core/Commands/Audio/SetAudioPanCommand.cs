namespace JazzHands.Core.Commands;

/// <summary>Sets where an audio clip sits between left and right.</summary>
/// <remarks>
/// A mono clip is panned with the constant power law, so it is as loud wherever it sits. A stereo
/// clip is balanced: the side it moves away from is turned down, and centre leaves both alone.
/// </remarks>
/// <param name="ClipId">Which clip. It must be on an audio track.</param>
/// <param name="Pan">-1 is hard left, 0 centre, 1 hard right.</param>
[Command("audio.set-pan", Description = "Set an audio clip's pan")]
public sealed record SetAudioPanCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("pan", "-1 hard left, 0 centre, 1 hard right")] double Pan) : ICommand;
