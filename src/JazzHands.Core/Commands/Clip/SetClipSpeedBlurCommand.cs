namespace JazzHands.Core.Commands;

/// <summary>Whether a clip's picture blurs with its speed.</summary>
/// <remarks>
/// On, each frame averages the clip's source across the shutter (the clip's motion blur angle, or
/// 180 degrees), so where a speed ramp runs fast the picture streaks as a camera's would at that
/// speed. Where the source moves less than one of its frames under the shutter (normal speed, for
/// a recording at the timeline's rate or slower) the picture stays exactly as it was.
/// The source's own motion is what blurs, unlike motion blur on a clip's placement.
/// </remarks>
/// <param name="ClipId">Which clip.</param>
/// <param name="On">True to blur with the speed, false for off.</param>
[Command("clip.set-speed-blur", Description = "Blur a clip's picture with its speed, as a shutter would")]
public sealed record SetClipSpeedBlurCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Arg(1, "true to blur with the speed, false for off")] bool On) : ICommand;
