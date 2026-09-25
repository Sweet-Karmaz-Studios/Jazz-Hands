namespace JazzHands.Core.Commands;

/// <summary>Makes every marker on a sequence a chapter.</summary>
/// <remarks>For a cut marked up while watching it; markers on clips are left alone.</remarks>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
[Command("chapter.from-markers", Description = "Make every marker on a sequence a chapter")]
public sealed record ChaptersFromMarkersCommand(
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : ICommand;
