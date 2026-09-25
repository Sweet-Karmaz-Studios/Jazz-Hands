using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Puts a chapter mark on a sequence.</summary>
/// <remarks>
/// A chapter is a marker with its chapter flag set: it runs to the next chapter, or the end.
/// Exports write chapters into MP4 and Matroska files, where players list them and YouTube
/// reads them. <c>marker.set --chapter</c> makes any marker one.
/// </remarks>
/// <param name="Name">Its title.</param>
/// <param name="At">Where it starts.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
/// <param name="ChapterId">The identifier to give it.</param>
[Command("chapter.add", Description = "Put a chapter mark on a sequence")]
public sealed record AddChapterCommand(
    [property: Arg(0, "Its title")] string Name,
    [property: Option("at", "Where it starts")] Flicks At,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null,
    [property: Option("id", "The identifier to give it")] string? ChapterId = null) : ICommand;
