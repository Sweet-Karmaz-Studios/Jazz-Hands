namespace JazzHands.Core.Commands;

/// <summary>Whether a clip's sound keeps its pitch at a speed other than normal.</summary>
/// <remarks>
/// Kept, the sound is time stretched: faster speech sounds like the same voice talking faster.
/// Not kept, it follows the speed as tape does, higher when faster. New clips keep it; clips from a
/// project made before Phase 36 follow the speed, as they always did. Only the sound changes; a
/// clip at normal speed sounds the same either way.
/// </remarks>
/// <param name="ClipId">Which clip.</param>
/// <param name="Keep">True to keep the pitch, false for the pitch to follow the speed.</param>
[Command("clip.set-keep-pitch", Description = "Keep a clip's pitch at its speed, or let it follow the speed like tape")]
public sealed record SetClipKeepPitchCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Arg(1, "true to keep the pitch, false to let it follow the speed")] bool Keep) : ICommand;
