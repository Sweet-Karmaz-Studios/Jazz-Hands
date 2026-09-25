namespace JazzHands.Core.Commands;

/// <summary>Sets motion blur on a clip, over its track's and its sequence's.</summary>
/// <remarks>
/// Motion blur averages the clip drawn at moments across the shutter, so a clip whose transform,
/// crop or masks are keyframed (or a title animating its own parameters) streaks as it moves. A
/// clip that does not move draws exactly as without it. <c>--off</c> turns it off for this clip
/// even where the track or sequence has it on; <c>--inherit</c> takes the clip's own setting away
/// so it follows them again.
/// </remarks>
/// <param name="ClipId">Which clip.</param>
/// <param name="Angle">How long the shutter is open, in degrees of a frame: 180 is half a frame.</param>
/// <param name="Samples">How many moments across the shutter are averaged, 2 to 64.</param>
/// <param name="Off">Turn it off here.</param>
/// <param name="Inherit">Follow the track and sequence instead.</param>
[Command("clip.set-motion-blur", Description = "Set motion blur on a clip's animated movement")]
public sealed record SetClipMotionBlurCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("angle", "Shutter angle in degrees: 180 is half a frame. Default: 180")] double? Angle = null,
    [property: Option("samples", "Moments averaged, 2 to 64. Default: 32")] int? Samples = null,
    [property: Option("off", "Turn it off for this clip")] bool Off = false,
    [property: Option("inherit", "Follow the track and sequence instead")] bool Inherit = false) : ICommand;
