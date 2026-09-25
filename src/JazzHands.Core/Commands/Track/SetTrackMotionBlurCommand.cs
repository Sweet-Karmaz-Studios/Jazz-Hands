namespace JazzHands.Core.Commands;

/// <summary>Sets motion blur for the animated clips on a track, over the sequence's.</summary>
/// <param name="TrackId">Which track.</param>
/// <param name="Angle">How long the shutter is open, in degrees of a frame: 180 is half a frame.</param>
/// <param name="Samples">How many moments across the shutter are averaged, 2 to 64.</param>
/// <param name="Off">Turn it off on this track.</param>
/// <param name="Inherit">Follow the sequence instead.</param>
[Command("track.set-motion-blur", Description = "Set motion blur for the animated clips on a track")]
public sealed record SetTrackMotionBlurCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Option("angle", "Shutter angle in degrees: 180 is half a frame. Default: 180")] double? Angle = null,
    [property: Option("samples", "Moments averaged, 2 to 64. Default: 32")] int? Samples = null,
    [property: Option("off", "Turn it off on this track")] bool Off = false,
    [property: Option("inherit", "Follow the sequence instead")] bool Inherit = false) : ICommand;
