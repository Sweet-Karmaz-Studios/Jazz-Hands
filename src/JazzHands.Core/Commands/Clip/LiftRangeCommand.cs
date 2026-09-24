using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Takes out what is between two times and leaves a gap.</summary>
/// <remarks>Clips that cross either time are cut there, so only the part inside goes.</remarks>
/// <param name="From">Where the range starts.</param>
/// <param name="To">Where it ends.</param>
/// <param name="TrackIds">Which tracks. Every unlocked track when left out.</param>
/// <param name="SequenceId">Which sequence. The active one when left out.</param>
[Command("clip.lift", Description = "Remove a range, leaving a gap")]
public sealed record LiftRangeCommand(
    [property: Option("from", "Where the range starts")] Flicks From,
    [property: Option("to", "Where it ends")] Flicks To,
    [property: Option("tracks", "Comma-separated track ids; every unlocked track when left out")] EquatableArray<string> TrackIds = default,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : ICommand;
