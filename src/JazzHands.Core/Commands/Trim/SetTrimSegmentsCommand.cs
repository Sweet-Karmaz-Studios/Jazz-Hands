using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Keeps exactly these stretches of a Quick Trim's file and cuts the rest.</summary>
/// <remarks>
/// Stretches are in source time, snapped to whole frames; overlapping and touching ones are
/// joined. On the command line they are written <c>00:10-00:25,01:00-01:30</c>.
/// </remarks>
/// <param name="Keep">The stretches to keep.</param>
/// <param name="SequenceId">The Quick Trim sequence. Defaults to the active one.</param>
[Command("trim.set-segments", Description = "Keep exactly these stretches of a Quick Trim")]
public sealed record SetTrimSegmentsCommand(
    [property: Arg(0, "The stretches to keep, as start-end pairs")] EquatableArray<TimeRange> Keep,
    [property: Option("sequence", "The Quick Trim sequence")] string? SequenceId = null) : ICommand;
