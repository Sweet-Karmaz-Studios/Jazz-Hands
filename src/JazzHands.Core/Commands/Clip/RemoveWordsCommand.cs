namespace JazzHands.Core.Commands;

/// <summary>Cuts words out of a clip: text-based editing.</summary>
/// <remarks>
/// Takes out the stretch from the first word's start to the start of the word after the last
/// (the pause after them goes too, so what is left runs on naturally), each edge on the nearest
/// frame, and closes the gap: on the clip's track, the tracks of the clips linked to it (its
/// sound), the unlocked subtitle tracks (captions of those words), and every sync-locked track.
/// Words are numbered as <c>speech.transcript</c> gives them for the clip. One undo.
/// </remarks>
/// <param name="ClipId">The clip, or a clip linked to it.</param>
/// <param name="From">The first word to take out, by its index.</param>
/// <param name="To">The last word to take out, by its index; the first when left out.</param>
[Command("clip.remove-words", Description = "Cut words out of a clip, closing the gap")]
public sealed record RemoveWordsCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("from", "The first word's index, from speech.transcript")] int From,
    [property: Option("to", "The last word's index; the first when left out")] int? To = null) : ICommand;
