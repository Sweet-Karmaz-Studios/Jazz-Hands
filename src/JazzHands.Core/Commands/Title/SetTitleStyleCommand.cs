namespace JazzHands.Core.Commands;

/// <summary>Changes how a title looks: font, size, colour, place, box, outline and shadow.</summary>
/// <remarks>
/// Each option is a <c>gen.title</c> parameter set to a constant; those left out stay as they are.
/// A preset restyles the title first (its look and place, not its text or animation), and the
/// other options apply over it. A parameter with keyframes is refused rather than flattened, as
/// <c>param.set</c> does. Distances are sequence pixels.
/// </remarks>
/// <param name="ClipId">The title clip.</param>
/// <param name="Preset">A preset whose look to take.</param>
/// <param name="Font">The font family.</param>
/// <param name="Weight">thin, extra-light, light, regular, medium, semibold, bold, extra-bold or black.</param>
/// <param name="Italic">Slanted.</param>
/// <param name="Size">Text height.</param>
/// <param name="Color">The fill colour.</param>
/// <param name="Align">left, centre or right.</param>
/// <param name="VAlign">top, middle or bottom.</param>
/// <param name="Position">Where the text block's anchor sits, from the frame centre.</param>
/// <param name="Width">The width lines wrap at; 0 for none.</param>
/// <param name="LineSpacing">Line height as a multiple of the font's.</param>
/// <param name="Tracking">Extra space between letters.</param>
/// <param name="Stroke">The outline: a width, then optionally a colour.</param>
/// <param name="Box">The box's colour.</param>
/// <param name="BoxPadding">Space between the text and the box's edge.</param>
/// <param name="BoxRadius">The box's corner radius.</param>
/// <param name="Shadow">The shadow's colour.</param>
/// <param name="ShadowOffset">How far the shadow falls, across and down.</param>
/// <param name="ShadowBlur">How soft the shadow is.</param>
[Command("title.set-style", Description = "Change a title's font, size, colour, place, box, outline or shadow")]
public sealed record SetTitleStyleCommand(
    [property: Arg(0, "The title clip id")] string ClipId,
    [property: Option("preset", "Take a preset's look and place first")] string? Preset = null,
    [property: Option("font", "The font family")] string? Font = null,
    [property: Option("weight", "thin, extra-light, light, regular, medium, semibold, bold, extra-bold or black")] string? Weight = null,
    [property: Option("italic", "Slanted")] bool? Italic = null,
    [property: Option("size", "Text height in sequence pixels")] string? Size = null,
    [property: Option("color", "The fill colour")] string? Color = null,
    [property: Option("align", "left, centre or right")] string? Align = null,
    [property: Option("valign", "top, middle or bottom")] string? VAlign = null,
    [property: Option("position", "Where the text sits from the frame centre, as 'x, y' in sequence pixels")] string? Position = null,
    [property: Option("width", "The width lines wrap at; 0 for none")] string? Width = null,
    [property: Option("line-spacing", "Line height as a multiple of the font's")] string? LineSpacing = null,
    [property: Option("tracking", "Extra space between letters, in sequence pixels")] string? Tracking = null,
    [property: Option("stroke", "The outline: a width in pixels, then optionally a colour, as '4' or '4 #000000'")] string? Stroke = null,
    [property: Option("box", "The box's colour; #00000000 for none")] string? Box = null,
    [property: Option("box-padding", "Space between the text and the box's edge")] string? BoxPadding = null,
    [property: Option("box-radius", "The box's corner radius")] string? BoxRadius = null,
    [property: Option("shadow", "The shadow's colour; #00000000 for none")] string? Shadow = null,
    [property: Option("shadow-offset", "How far the shadow falls, as 'x, y'")] string? ShadowOffset = null,
    [property: Option("shadow-blur", "How soft the shadow is")] string? ShadowBlur = null) : ICommand;
