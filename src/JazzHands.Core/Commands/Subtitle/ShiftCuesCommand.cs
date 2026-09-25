using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Moves a subtitle track's cues earlier or later.</summary>
/// <remarks>For subtitles timed against a cut with a different start. A cue moved before the start of the sequence is refused, not cut.</remarks>
/// <param name="TrackId">The subtitle track.</param>
/// <param name="By">How far: positive is later, negative earlier.</param>
/// <param name="From">Only the cues that start at or after this time.</param>
[Command("subtitle.shift", Description = "Move a subtitle track's cues earlier or later")]
public sealed record ShiftCuesCommand(
    [property: Arg(0, "The subtitle track id")] string TrackId,
    [property: Option("by", "How far; negative is earlier")] Flicks By,
    [property: Option("from", "Only cues from this time on")] Flicks? From = null) : ICommand;
