using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>What the source monitor holds: the item, its playhead and its marks.</summary>
[Query("source.state", Description = "What the source monitor has open, and its marks")]
public sealed record GetSourceStateQuery : IQuery<SourceStateInfo>;

/// <summary>The source monitor's state.</summary>
/// <param name="MediaId">The item open, or null when nothing is.</param>
/// <param name="Name">Its name.</param>
/// <param name="Position">The source playhead, in source time.</param>
/// <param name="In">The in mark, or null.</param>
/// <param name="Out">The out mark, exclusive (one frame past the marked frame), or null.</param>
/// <param name="Duration">The file's length.</param>
/// <param name="FrameRate">Its picture's frame rate, or null for sound.</param>
/// <param name="Timecode">The playhead as timecode.</param>
/// <param name="IsPlaying">True while it plays.</param>
public sealed record SourceStateInfo(
    string? MediaId,
    string? Name,
    Flicks Position,
    Flicks? In,
    Flicks? Out,
    Flicks Duration,
    Rational? FrameRate,
    string Timecode,
    bool IsPlaying);
