namespace JazzHands.Core.Commands;

/// <summary>
/// Where the preview's composition thread spent its time on the frames it rendered while playing,
/// since the timings were last reset: the per frame breakdown that says what to make faster.
/// </summary>
/// <param name="Frames">Frames rendered while playing.</param>
/// <param name="Late">How far into its interval each frame's render began, the clock's time after the frame was due.</param>
/// <param name="Fetch">Getting the layers' pictures during the render: cache lookups and any decode that had to happen then.</param>
/// <param name="Render">The whole render, fetch included: building the graph, compositing, the output pass.</param>
/// <param name="Present">Handing the frame to the preview targets: the blit and whatever the targets wait on.</param>
/// <param name="Ahead">Decoding ahead after the present, per frame.</param>
/// <param name="DecodedInRender">Pictures decoded because a render needed them and they were not ready: each one is time the frame waited.</param>
/// <param name="DecodedAhead">Pictures decoded ahead, before they were needed.</param>
public sealed record FrameTimingsInfo(
    long Frames,
    StageTiming Late,
    StageTiming Fetch,
    StageTiming Render,
    StageTiming Present,
    StageTiming Ahead,
    long DecodedInRender,
    long DecodedAhead)
{
    /// <summary>Nothing rendered yet.</summary>
    public static FrameTimingsInfo None { get; } = new(0, default, default, default, default, default, 0, 0);
}

/// <summary>One stage's time per frame, in milliseconds.</summary>
/// <param name="P50">The median.</param>
/// <param name="P99">The 99th percentile.</param>
/// <param name="Max">The longest.</param>
/// <param name="Mean">The mean.</param>
public readonly record struct StageTiming(double P50, double P99, double Max, double Mean);
