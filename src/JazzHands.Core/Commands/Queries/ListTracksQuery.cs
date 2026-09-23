namespace JazzHands.Core.Commands;

/// <summary>Asks for the tracks of a sequence, bottom of the stack first.</summary>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
[Query("track.list", Description = "List the tracks of a sequence")]
public sealed record ListTracksQuery(
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : IQuery<TrackInfo[]>;
