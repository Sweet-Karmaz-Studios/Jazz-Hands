namespace JazzHands.Core.Commands;

/// <summary>Takes noise, room and hum out of the speech in a clip.</summary>
/// <remarks>
/// Runs the clip's file's sound through DeepFilterNet 3 on this computer (once per file, kept in
/// the cache; about three seconds a minute) and puts the <c>audio.enhance-speech</c> effect on
/// the clip, whose amount mixes the original back in. Needs the <c>deepfilternet3</c> model
/// (<c>model.download</c>, 8 MB). <c>--off</c> takes the effect away. One undo.
/// </remarks>
/// <param name="ClipId">The clip, of a file with sound.</param>
/// <param name="Amount">How much of the enhanced speech is heard, 0 to 100.</param>
/// <param name="Off">Take the effect off instead.</param>
[Command("audio.enhance-speech", Description = "Take noise, room and hum out of speech")]
public sealed record EnhanceSpeechCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("amount", "How much of the enhanced speech is heard, 0 to 100. Default: 100")] double Amount = 100,
    [property: Option("off", "Take the effect off instead")] bool Off = false) : ICommand;
