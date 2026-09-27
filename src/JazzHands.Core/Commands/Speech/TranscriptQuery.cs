namespace JazzHands.Core.Commands;

/// <summary>The words said in a clip or a sequence, at their timeline times.</summary>
/// <remarks>
/// Reads the transcripts <c>speech.transcribe</c> made: for a clip, the words inside its in and
/// out at the times its speed puts them; for a sequence, the words of every sounding clip, in
/// timeline order. Each word has its index in its clip, which <c>clip.remove-words</c> takes.
/// <c>text</c> is the same as a reader would read it, with the indices and the long pauses.
/// </remarks>
/// <param name="TargetId">A clip or sequence; the active sequence when left out.</param>
[Query("speech.transcript", Description = "The words said in a clip or sequence, with their times")]
public sealed record TranscriptQuery(
    [property: Arg(0, "A clip or sequence id; the active sequence when left out")] string? TargetId = null) : IQuery<TranscriptInfo>;
