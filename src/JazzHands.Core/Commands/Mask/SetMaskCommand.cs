using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Changes a mask. Only what is given changes.</summary>
/// <param name="MaskId">Which mask.</param>
/// <param name="Shape">rectangle, ellipse, polygon or bezier.</param>
/// <param name="X">Left edge of the bounds.</param>
/// <param name="Y">Top edge of the bounds.</param>
/// <param name="Width">Width of the bounds.</param>
/// <param name="Height">Height of the bounds.</param>
/// <param name="Path">The outline of a polygon or bezier.</param>
/// <param name="Feather">Softening of the edge, in sequence pixels.</param>
/// <param name="Opacity">How strongly it applies, 0 to 1.</param>
/// <param name="Mode">add, subtract or intersect.</param>
/// <param name="Invert">Keep the outside instead.</param>
/// <param name="Expansion">Grow the shape by this many sequence pixels; shrink it when negative.</param>
/// <param name="Enabled">Switch it on or off without removing it.</param>
[Command("mask.set", Description = "Change a mask")]
public sealed record SetMaskCommand(
    [property: Arg(0, "The mask id")] string MaskId,
    [property: Option("shape", "rectangle, ellipse, polygon or bezier")] MaskShape? Shape = null,
    [property: Option("x", "Left edge of the bounds, source pixels")] double? X = null,
    [property: Option("y", "Top edge of the bounds")] double? Y = null,
    [property: Option("width", "Width of the bounds")] double? Width = null,
    [property: Option("height", "Height of the bounds")] double? Height = null,
    [property: Option("path", "SVG path data for a polygon or bezier")] string? Path = null,
    [property: Option("feather", "Edge softening in pixels")] double? Feather = null,
    [property: Option("opacity", "0 to 1")] double? Opacity = null,
    [property: Option("mode", "add, subtract or intersect")] MaskMode? Mode = null,
    [property: Option("invert", "Keep the outside instead")] bool? Invert = null,
    [property: Option("expansion", "Grow the shape by this many pixels; negative shrinks it")] double? Expansion = null,
    [property: Option("enabled", "on or off")] bool? Enabled = null) : ICommand;
