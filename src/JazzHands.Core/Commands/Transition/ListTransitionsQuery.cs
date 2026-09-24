namespace JazzHands.Core.Commands;

/// <summary>Lists the transitions of a sequence, or of one track, with where each plays.</summary>
/// <param name="TrackId">Only this track.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
[Query("transition.list", Description = "List transitions and where they play")]
public sealed record ListTransitionsQuery(
    [property: Option("track", "Only this track")] string? TrackId = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : IQuery<TransitionInfo[]>;
