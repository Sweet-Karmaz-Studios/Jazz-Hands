namespace JazzHands.Core.Commands;

/// <summary>Turns speech into words with their times, on this machine.</summary>
/// <remarks>
/// Hears the sound of a media item, a clip's file, or every file the sounding clips of a sequence
/// play, with whisper large-v3-turbo (<c>model.download</c> fetches it first, 1.6 GB). A
/// transcript is of the whole file and kept in the cache by its content, so every clip of it
/// shares it and asking again is instant; <c>--again</c> hears it anew. Ten minutes take about
/// fifteen seconds on a recent GPU, a minute on the CPU. The project does not change;
/// <c>speech.transcript</c> reads the words.
/// </remarks>
/// <param name="TargetId">A media item, clip or sequence; the active sequence when left out.</param>
/// <param name="Stream">Which sound stream of a media item, by its index in the file; the first when left out.</param>
/// <param name="Language">The language as an ISO 639-1 code (en, de, fr); <c>auto</c> to detect it.</param>
/// <param name="Again">Hear it again even when a transcript is cached.</param>
[Command("speech.transcribe", Description = "Transcribe speech into words with their times",
    Undoable = false,
    NotUndoableReason = "A transcript is kept in the cache, like an analysis. The project does not change.")]
public sealed record TranscribeCommand(
    [property: Arg(0, "A media, clip or sequence id; the active sequence when left out")] string? TargetId = null,
    [property: Option("stream", "The sound stream's index in the file")] int? Stream = null,
    [property: Option("language", "An ISO 639-1 code such as en, or auto. Default: en")] string Language = "en",
    [property: Option("again", "Transcribe again even when cached")] bool Again = false) : ICommand;
