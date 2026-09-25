namespace JazzHands.Core.Commands;

/// <summary>A subtitle track's cues, in time order.</summary>
/// <param name="TrackId">The subtitle track.</param>
[Query("subtitle.list", Description = "List a subtitle track's cues")]
public sealed record ListCuesQuery(
    [property: Arg(0, "The subtitle track id")] string TrackId) : IQuery<CueInfo[]>;
