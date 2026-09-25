using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Makes a media item's own chapters into chapter marks on a sequence.</summary>
/// <remarks>
/// For a recording that already has chapters, from OBS or an earlier export. The file's time 0
/// lands at <c>--at</c> on the sequence; a chapter already at the same frame is left as it is.
/// </remarks>
/// <param name="MediaId">The media item.</param>
/// <param name="At">Where the file's start lands on the sequence; the start when not given.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
[Command("chapter.import", Description = "Make a media item's chapters into chapter marks")]
public sealed record ImportChaptersCommand(
    [property: Arg(0, "The media item id")] string MediaId,
    [property: Option("at", "Where the file's start lands")] Flicks? At = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : ICommand;
