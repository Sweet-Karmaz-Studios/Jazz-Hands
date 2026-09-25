using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>The packaged speed ramps.</summary>
public enum SpeedPreset
{
    /// <summary>Slow down into the moment and snap back after it: the hit in slow motion.</summary>
    Impact,

    /// <summary>Speed through a stretch and ease back: a long walk, a climb, a menu.</summary>
    Traversal,

    /// <summary>The last seconds played backwards, fast, with the VHS look, on a track above.</summary>
    Rewind,

    /// <summary>The last seconds again in slow motion with a REPLAY label, on a track above.</summary>
    Replay,
}

/// <summary>Puts a packaged speed ramp on a clip of a file at a moment.</summary>
/// <remarks>
/// <para>
/// <c>impact</c> ramps down to a fifth of the speed over the half second before <c>--at</c>,
/// holds a moment, and snaps back. <c>traversal</c> ramps up to three times over a third of a
/// second from <c>--at</c>, runs fast for <c>--dur</c>, and eases back. Both are the clip's time
/// remap curve (the speed parameter, <c>remap</c>), keyframes to adjust afterwards, and blend
/// frames where the source is slowed.
/// </para>
/// <para>
/// <c>rewind</c> and <c>replay</c> leave the clip as it is and add a clip on a new track above
/// from <c>--at</c>, over what follows: the <c>--dur</c> seconds before the moment played
/// backwards at four times with <c>video.vhs</c> and a little glitch, or played again at half
/// speed, blended, between letterbox bars, under a REPLAY label. One undo.
/// </para>
/// </remarks>
/// <param name="ClipId">The clip, a clip of a video file.</param>
/// <param name="Preset">impact, traversal, rewind or replay.</param>
/// <param name="At">The moment on the sequence: the hit, the start, or the end of what is repeated.</param>
/// <param name="Duration">traversal: how long it runs fast; rewind and replay: how much is repeated. Two seconds when not given.</param>
[Command("clip.speed-preset", Description = "Put a packaged speed ramp on a clip: slow into an impact and snap back, speed through a traversal, a VHS rewind, or a slow motion replay")]
public sealed record SpeedPresetCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Arg(1, "impact, traversal, rewind or replay")] SpeedPreset Preset,
    [property: Option("at", "The moment: the hit, the start, or the end of what is repeated")] Flicks At,
    [property: Option("dur", "How long it runs fast, or how much is repeated. Default: 2s")] Flicks? Duration = null) : ICommand;
