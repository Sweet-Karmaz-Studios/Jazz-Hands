namespace JazzHands.Core.Commands;

/// <summary>Plays a clip backwards, or forwards again.</summary>
/// <param name="ClipId">Which clip.</param>
/// <param name="Reverse">True to play it backwards.</param>
[Command("clip.set-reverse", Description = "Play a clip backwards")]
public sealed record SetClipReverseCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Arg(1, "true to play it backwards")] bool Reverse) : ICommand;
