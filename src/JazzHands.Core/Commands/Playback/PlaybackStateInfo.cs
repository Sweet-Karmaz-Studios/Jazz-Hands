using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>What playback is doing.</summary>
/// <param name="State">playing, paused or stopped.</param>
/// <param name="SequenceId">The sequence being played.</param>
/// <param name="Position">Where the playhead is, as heard.</param>
/// <param name="Timecode">The same, as timecode on the sequence's frame grid.</param>
/// <param name="Frame">The frame at the playhead, counted from the start of the sequence.</param>
/// <param name="Rate">1 for normal speed, negative for backwards.</param>
/// <param name="Loop">True when playback loops over the in and out points.</param>
/// <param name="Quality">The quality asked for.</param>
/// <param name="EffectiveQuality">What Auto resolved to, or the same as Quality.</param>
/// <param name="In">The in point, when there is one.</param>
/// <param name="Out">The first instant after the out point, when there is one.</param>
/// <param name="PresentedFrames">Frames put on screen since the editor started.</param>
/// <param name="DroppedFrames">Frames that were due at normal speed and never shown.</param>
/// <param name="StretchedAudio">True when rates other than 1 keep their pitch; false when they play silent.</param>
/// <param name="Render">What the render pools have done, to show that steady playback allocates nothing.</param>
/// <param name="ProxiesEnabled">True when proxies play in place of their sources.</param>
/// <param name="Timings">Where the time of each played frame went, since the editor started or the timings were reset.</param>
public sealed record PlaybackStateInfo(
    string State,
    string? SequenceId,
    Flicks Position,
    string Timecode,
    long Frame,
    double Rate,
    bool Loop,
    PreviewQuality Quality,
    PreviewQuality EffectiveQuality,
    Flicks? In,
    Flicks? Out,
    long PresentedFrames,
    long DroppedFrames,
    bool StretchedAudio,
    RenderStatsInfo? Render = null,
    bool ProxiesEnabled = false,
    FrameTimingsInfo? Timings = null);
