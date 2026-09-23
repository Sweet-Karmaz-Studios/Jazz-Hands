namespace JazzHands.Core.Commands;

/// <summary>Asks for clips, in timeline order.</summary>
/// <param name="TrackId">Only this track. Every track when left out.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
[Query("clip.list", Description = "List clips, in timeline order")]
public sealed record ListClipsQuery(
    [property: Option("track", "Only this track")] string? TrackId = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : IQuery<ClipInfo[]>;
