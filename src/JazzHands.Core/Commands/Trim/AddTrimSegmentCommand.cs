using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Keeps one more stretch of a Quick Trim's file, joining any it overlaps.</summary>
/// <param name="In">Where the stretch starts, in source time.</param>
/// <param name="Out">Where it ends, exclusive.</param>
/// <param name="SequenceId">The Quick Trim sequence. Defaults to the active one.</param>
[Command("trim.add-segment", Description = "Keep a stretch of a Quick Trim")]
public sealed record AddTrimSegmentCommand(
    [property: Option("in", "Where the stretch starts")] Flicks In,
    [property: Option("out", "Where it ends")] Flicks Out,
    [property: Option("sequence", "The Quick Trim sequence")] string? SequenceId = null) : ICommand;
