using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>How a shot gave way to the next.</summary>
public enum SceneCutKind
{
    /// <summary>From one frame to the next.</summary>
    Cut,

    /// <summary>Over several frames, one picture mixing into the other (a dissolve, a fade).</summary>
    Dissolve,
}

/// <summary>One shot change <c>media.detect-cuts</c> found.</summary>
/// <param name="Time">The source time of the new shot's first frame, or of a dissolve's middle frame.</param>
/// <param name="Timecode">The same, as timecode at the file's frame rate.</param>
/// <param name="Score">How much changed, 0 to 100: the percentage of the picture's range.</param>
/// <param name="Kind">A cut or a dissolve.</param>
public sealed record SceneCutInfo(Flicks Time, string Timecode, double Score, SceneCutKind Kind);
