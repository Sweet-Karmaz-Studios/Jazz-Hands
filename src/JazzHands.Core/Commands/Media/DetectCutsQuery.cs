using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Finds the shot changes in an already edited video.</summary>
/// <remarks>
/// Reads the whole file once, which takes about a third of its running time for 1080p60 H.264,
/// and keeps what it measured in the cache by content hash, so asking again at another threshold
/// is instant. A dissolve is found once, at its middle.
/// </remarks>
/// <param name="MediaId">The media item.</param>
/// <param name="Threshold">How much of the picture has to change, 0 to 100; lower finds more.</param>
/// <param name="MinShot">The shortest shot there can be; a change closer than this to the last is dropped, keeping the stronger.</param>
/// <param name="Stream">Which video stream, by its index in the file; the first when left out.</param>
[Query("media.detect-cuts", Description = "Find the shot changes in an edited video")]
public sealed record DetectCutsQuery(
    [property: Arg(0, "The media id")] string MediaId,
    [property: Option("threshold", "How much has to change, 0 to 100; lower finds more. Default: 10")] double Threshold = SceneCuts.DefaultThreshold,
    [property: Option("min-shot", "The shortest shot there can be. Default: 0.5 s")] Flicks? MinShot = null,
    [property: Option("stream", "The video stream's index in the file")] int? Stream = null) : IQuery<SceneCutInfo[]>;

/// <summary>What the scene-cut commands share.</summary>
public static class SceneCuts
{
    /// <summary>The threshold when none is given: scdet's.</summary>
    public const double DefaultThreshold = 10;

    /// <summary>The shortest shot when none is given.</summary>
    public static Flicks DefaultMinShot { get; } = Flicks.FromMilliseconds(500);
}
