namespace JazzHands.Core.Commands;

/// <summary>Moves, scales and rotates a clip's picture.</summary>
/// <remarks>
/// Only what is given changes; the rest keeps its value. Position is in sequence pixels from the
/// frame centre, rotation is degrees clockwise, and the anchor, the point the picture scales and
/// turns about, is in the picture's own pixels from its centre. The values are static for now;
/// keyframes arrive with Phase 15's keyframe commands.
/// </remarks>
/// <param name="ClipId">Which clip. It must be on a video or adjustment track.</param>
/// <param name="X">Horizontal offset from the frame centre.</param>
/// <param name="Y">Vertical offset, down being positive.</param>
/// <param name="Scale">Both axes at once; 1 is the fitted size.</param>
/// <param name="ScaleX">The horizontal scale alone.</param>
/// <param name="ScaleY">The vertical scale alone.</param>
/// <param name="Rotation">Degrees clockwise.</param>
/// <param name="AnchorX">The pivot's horizontal offset from the picture's centre.</param>
/// <param name="AnchorY">The pivot's vertical offset.</param>
[Command("clip.set-transform", Description = "Move, scale or rotate a clip's picture")]
public sealed record SetClipTransformCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("x", "Pixels right of the frame centre")] double? X = null,
    [property: Option("y", "Pixels below the frame centre")] double? Y = null,
    [property: Option("scale", "Both axes; 1 is the fitted size")] double? Scale = null,
    [property: Option("scale-x", "The horizontal scale alone")] double? ScaleX = null,
    [property: Option("scale-y", "The vertical scale alone")] double? ScaleY = null,
    [property: Option("rotation", "Degrees clockwise")] double? Rotation = null,
    [property: Option("anchor-x", "The pivot, picture pixels right of its centre")] double? AnchorX = null,
    [property: Option("anchor-y", "The pivot, picture pixels below its centre")] double? AnchorY = null) : ICommand;
