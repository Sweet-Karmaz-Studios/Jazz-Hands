namespace JazzHands.Core.Commands;

/// <summary>Sets how opaque a clip's picture is.</summary>
/// <param name="ClipId">Which clip. It must be on a video or adjustment track.</param>
/// <param name="Opacity">0 is invisible, 1 fully opaque.</param>
[Command("clip.set-opacity", Description = "Set how opaque a clip's picture is")]
public sealed record SetClipOpacityCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("opacity", "0 invisible to 1 opaque")] double Opacity) : ICommand;
