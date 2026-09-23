namespace JazzHands.Core.Commands;

/// <summary>Sets an audio clip's gain.</summary>
/// <remarks>
/// A static gain in decibels, replacing any automation. Keyframed gain is the keyframe
/// commands' job; this is the fader.
/// </remarks>
/// <param name="ClipId">Which clip. It must be on an audio track.</param>
/// <param name="Db">Gain in decibels, from -144 (silence) to +24. Zero leaves the level alone.</param>
[Command("audio.set-gain", Description = "Set an audio clip's gain in dB")]
public sealed record SetAudioGainCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("db", "Gain in dB, -144 to 24")] double Db) : ICommand;
