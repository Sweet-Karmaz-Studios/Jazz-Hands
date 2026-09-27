namespace JazzHands.Core.Commands;

/// <summary>Checks a subtitle track's cues are readable.</summary>
/// <remarks>
/// Line length and line count (from the track's style), reading speed (20 characters a second,
/// spaces counted), time on screen (five sixths of a second to seven seconds) and the gap between
/// cues (two frames, or none). An empty answer means every cue passes.
/// </remarks>
/// <param name="TrackId">The subtitle track.</param>
/// <param name="MaxCharsPerSecond">The fastest reading speed allowed.</param>
[Query("subtitle.check", Description = "Check subtitle cues for line length, reading speed and gaps")]
public sealed record CheckCuesQuery(
    [property: Arg(0, "The subtitle track id")] string TrackId,
    [property: Option("max-cps", "The most characters a second. Default: 20")] double MaxCharsPerSecond = 20) : IQuery<CueProblemInfo[]>;
