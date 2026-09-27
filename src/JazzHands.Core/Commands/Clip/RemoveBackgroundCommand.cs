namespace JazzHands.Core.Commands;

/// <summary>Cuts a person out of a clip's picture without a green screen.</summary>
/// <remarks>
/// Makes the person matte of the clip's file with Robust Video Matting on this computer (once per
/// file, kept in the cache; about a minute for a minute of 1080p60 on a recent GPU) and puts the
/// <c>video.matte.person</c> effect first on the clip, whose choke, feather, invert and matte view
/// then work as a keyer's do. Needs the <c>rvm-mobilenetv3</c> model (<c>model.download</c>, 15 MB).
/// <c>--off</c> takes the effect away; the matte stays in the cache. One undo.
/// </remarks>
/// <param name="ClipId">The clip, of a video file.</param>
/// <param name="Off">Take the effect off instead.</param>
[Command("clip.remove-background", Description = "Cut a person out of a clip without a green screen")]
public sealed record RemoveBackgroundCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("off", "Take the effect off instead")] bool Off = false) : ICommand;
