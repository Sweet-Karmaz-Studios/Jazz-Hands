namespace JazzHands.Core.Commands;

/// <summary>Swaps what an audio clip plays for a stream of another media item.</summary>
/// <remarks>
/// The clip keeps its place, its length, its in point, its gain, pan and fades, and its links:
/// only the source changes. The new source has to be long enough to cover what the clip plays.
/// </remarks>
/// <param name="ClipId">Which clip. It must be on an audio track.</param>
/// <param name="MediaId">The media item to play instead.</param>
/// <param name="Stream">Which of its streams. Defaults to its first audio stream.</param>
[Command("audio.replace", Description = "Play another media item's audio in a clip")]
public sealed record ReplaceAudioCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Arg(1, "The media item to play instead")] string MediaId,
    [property: Option("stream", "Which audio stream of it; the first when left out")] int? Stream = null) : ICommand;
