namespace JazzHands.Core.Commands;

/// <summary>Asks for markers, in time order.</summary>
/// <param name="ClipId">Only the markers on this clip.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
/// <param name="ChaptersOnly">Only the markers that export as chapters.</param>
[Query("marker.list", Description = "List markers, in time order")]
public sealed record ListMarkersQuery(
    [property: Option("clip", "Only the markers on this clip")] string? ClipId = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null,
    [property: Option("chapters", "Only chapter markers")] bool ChaptersOnly = false) : IQuery<MarkerInfo[]>;
