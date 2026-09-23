using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Limits what of a clip is seen to a shape.</summary>
/// <remarks>
/// Coordinates are the clip's own source pixels. A rectangle or ellipse takes its bounds
/// (<c>--x --y --width --height</c>); a polygon or bezier takes <c>--path</c> in SVG path syntax,
/// for example <c>M 100 100 L 500 100 L 300 400 Z</c>. Several masks on one clip combine in
/// order by their modes.
/// </remarks>
/// <param name="ClipId">Which clip. It must be on a video or adjustment track.</param>
/// <param name="Shape">rectangle, ellipse, polygon or bezier.</param>
/// <param name="X">Left edge of the bounds.</param>
/// <param name="Y">Top edge of the bounds.</param>
/// <param name="Width">Width of the bounds.</param>
/// <param name="Height">Height of the bounds.</param>
/// <param name="Path">The outline of a polygon or bezier.</param>
/// <param name="Feather">Softening of the edge, in sequence pixels.</param>
/// <param name="Opacity">How strongly it applies, 0 to 1.</param>
/// <param name="Mode">add, subtract or intersect with the masks before it.</param>
/// <param name="Invert">Keep the outside instead.</param>
/// <param name="MaskId">The identifier to give it.</param>
[Command("mask.add", Description = "Limit a clip's picture to a shape")]
public sealed record AddMaskCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("shape", "rectangle, ellipse, polygon or bezier")] MaskShape Shape,
    [property: Option("x", "Left edge of the bounds, source pixels")] double? X = null,
    [property: Option("y", "Top edge of the bounds")] double? Y = null,
    [property: Option("width", "Width of the bounds")] double? Width = null,
    [property: Option("height", "Height of the bounds")] double? Height = null,
    [property: Option("path", "SVG path data for a polygon or bezier")] string? Path = null,
    [property: Option("feather", "Edge softening in pixels")] double Feather = 0,
    [property: Option("opacity", "0 to 1")] double Opacity = 1,
    [property: Option("mode", "add, subtract or intersect")] MaskMode Mode = MaskMode.Add,
    [property: Option("invert", "Keep the outside instead")] bool Invert = false,
    [property: Option("id", "The identifier to give it")] string? MaskId = null) : ICommand;
