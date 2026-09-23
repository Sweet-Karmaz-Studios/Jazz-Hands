using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Sets how a clip's picture combines with what is underneath.</summary>
/// <param name="ClipId">Which clip. It must be on a video or adjustment track.</param>
/// <param name="Mode">normal, add, multiply, screen, overlay, darken, lighten, difference, soft-light or hard-light.</param>
[Command("clip.set-blend", Description = "Set how a clip blends with what is under it")]
public sealed record SetClipBlendCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Arg(1, "normal, add, multiply, screen, overlay, darken, lighten, difference, soft-light or hard-light")] BlendMode Mode) : ICommand;
