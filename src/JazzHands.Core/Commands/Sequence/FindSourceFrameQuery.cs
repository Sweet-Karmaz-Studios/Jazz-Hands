using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Finds where in a sequence a frame of a file is used: match frame the other way.</summary>
/// <param name="MediaId">The media item.</param>
/// <param name="At">The source time.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
[Query("sequence.find-source-frame", Description = "Find where a frame of a file is in the sequence")]
public sealed record FindSourceFrameQuery(
    [property: Arg(0, "The media id")] string MediaId,
    [property: Option("at", "The source time")] Flicks At,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : IQuery<SourceFrameInfo>;

/// <summary>Where a source frame is in a sequence.</summary>
/// <param name="ClipId">The clip that shows it.</param>
/// <param name="TrackId">Its track.</param>
/// <param name="Time">The timeline time it shows at.</param>
public sealed record SourceFrameInfo(string ClipId, string TrackId, Flicks Time);
