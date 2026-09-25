using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Uses another track as a clip's matte: the clip shows only through that track's picture.</summary>
/// <remarks>
/// Gameplay inside a title, a reveal through an animated shape. The matte track is hidden from the
/// output while anything uses it. <c>alpha</c> keeps the clip where the matte is opaque,
/// <c>luma</c> where it is bright, and the inverted modes the opposite. <c>--off</c> takes the
/// clip's own matte away, so it follows its track's again.
/// </remarks>
/// <param name="ClipId">Which clip.</param>
/// <param name="Source">The video track whose picture is the matte, in the same sequence.</param>
/// <param name="Mode">alpha, luma, alpha-inverted or luma-inverted.</param>
/// <param name="Off">Take the clip's matte away.</param>
[Command("clip.set-matte", Description = "Show a clip only through another track's picture (a track matte)")]
public sealed record SetClipMatteCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("source", "The track whose picture is the matte")] string? Source = null,
    [property: Option("mode", "alpha, luma, alpha-inverted or luma-inverted. Default: alpha")] TrackMatteMode Mode = TrackMatteMode.Alpha,
    [property: Option("off", "Take the clip's matte away")] bool Off = false) : ICommand;
