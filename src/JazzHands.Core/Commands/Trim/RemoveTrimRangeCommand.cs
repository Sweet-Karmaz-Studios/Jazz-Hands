using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Cuts a range out of a Quick Trim, from whichever kept stretches it crosses.</summary>
/// <param name="In">Where the cut starts, in source time.</param>
/// <param name="Out">Where it ends, exclusive.</param>
/// <param name="SequenceId">The Quick Trim sequence. Defaults to the active one.</param>
[Command("trim.remove-range", Description = "Cut a range out of a Quick Trim")]
public sealed record RemoveTrimRangeCommand(
    [property: Option("in", "Where the cut starts")] Flicks In,
    [property: Option("out", "Where it ends")] Flicks Out,
    [property: Option("sequence", "The Quick Trim sequence")] string? SequenceId = null) : ICommand;
