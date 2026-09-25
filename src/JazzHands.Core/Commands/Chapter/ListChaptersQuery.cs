namespace JazzHands.Core.Commands;

/// <summary>A sequence's chapters, each with where it starts and ends.</summary>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
[Query("chapter.list", Description = "List a sequence's chapters")]
public sealed record ListChaptersQuery(
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : IQuery<ChapterSummary[]>;
