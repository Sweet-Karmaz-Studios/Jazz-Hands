namespace JazzHands.Core.Commands;

/// <summary>Unlinks a clip's audio from its picture.</summary>
/// <remarks>
/// Afterwards the audio clips move on their own, which is how a line of dialogue is slid under a
/// different shot. They stay linked to each other, so a capture's game and microphone still move
/// together; the picture is on its own.
/// </remarks>
/// <param name="ClipId">The picture, or any clip linked to it.</param>
[Command("audio.detach", Description = "Unlink a clip's audio from its picture")]
public sealed record DetachAudioCommand(
    [property: Arg(0, "The clip id, or any clip linked to it")] string ClipId) : ICommand;
