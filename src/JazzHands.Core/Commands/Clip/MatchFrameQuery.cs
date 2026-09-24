using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Finds the source frame a clip shows at a timeline time.</summary>
/// <param name="ClipId">Which clip.</param>
/// <param name="At">The time on the timeline.</param>
[Query("clip.match-frame", Description = "Find the source and frame a clip shows at a time")]
public sealed record MatchFrameQuery(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("at", "The time on the timeline")] Flicks At) : IQuery<MatchFrameInfo>;

/// <summary>Where a clip's frame comes from.</summary>
/// <param name="ClipId">The clip.</param>
/// <param name="MediaId">The media item it plays, or null.</param>
/// <param name="SequenceId">The sequence it nests, or null.</param>
/// <param name="SourceTime">The time inside that source.</param>
/// <param name="Path">The media file, full, or empty for a nested sequence.</param>
public sealed record MatchFrameInfo(string ClipId, string? MediaId, string? SequenceId, Flicks SourceTime, string Path);
