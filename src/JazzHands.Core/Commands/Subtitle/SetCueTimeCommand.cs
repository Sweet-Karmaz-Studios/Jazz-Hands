using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Changes when a subtitle cue appears or goes.</summary>
/// <remarks>Each option changes only what it names: <c>--at</c> moves the cue and keeps its length, <c>--dur</c> or <c>--end</c> change its length.</remarks>
/// <param name="CueId">The cue.</param>
/// <param name="At">When it appears.</param>
/// <param name="Duration">How long it stays.</param>
/// <param name="End">When it goes, instead of a length.</param>
[Command("subtitle.set-time", Description = "Change when a subtitle cue appears or goes")]
public sealed record SetCueTimeCommand(
    [property: Arg(0, "The cue id")] string CueId,
    [property: Option("at", "When it appears")] Flicks? At = null,
    [property: Option("dur", "How long it stays")] Flicks? Duration = null,
    [property: Option("end", "When it goes")] Flicks? End = null) : ICommand;
