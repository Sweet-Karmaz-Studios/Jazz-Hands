namespace JazzHands.Core.Commands;

/// <summary>Changes how a subtitle track's cues look.</summary>
/// <remarks>
/// Each option changes only what it names. Sizes are fractions of the frame's height, so a style
/// reads the same at any resolution: a size of 0.045 is 49 pixels at 1080 lines. Colours are sRGB
/// hex; add two digits of alpha for a translucent box or shadow (<c>#000000B3</c>).
/// </remarks>
/// <param name="TrackId">The subtitle track.</param>
/// <param name="Font">The font family.</param>
/// <param name="Weight">regular, medium, semibold, bold and so on.</param>
/// <param name="Italic">Slanted.</param>
/// <param name="Size">Text height as a fraction of the frame's height.</param>
/// <param name="Color">The letters.</param>
/// <param name="Outline">The outline's colour.</param>
/// <param name="OutlineWidth">The outline's width as a fraction of the height; 0 for none.</param>
/// <param name="Box">A box behind each cue; #00000000 for none.</param>
/// <param name="Shadow">A soft shadow; #00000000 for none.</param>
/// <param name="Margin">The distance kept from the frame's edge, as a fraction of its height.</param>
/// <param name="MaxLines">The most lines a cue should have.</param>
/// <param name="MaxChars">The most characters a line should have.</param>
[Command("subtitle.set-style", Description = "Change how a subtitle track's cues look")]
public sealed record SetSubtitleStyleCommand(
    [property: Arg(0, "The subtitle track id")] string TrackId,
    [property: Option("font", "The font family")] string? Font = null,
    [property: Option("weight", "regular, semibold, bold and so on")] string? Weight = null,
    [property: Option("italic", "Slanted")] bool? Italic = null,
    [property: Option("size", "Text height as a fraction of the frame, such as 0.045")] double? Size = null,
    [property: Option("color", "The letters, sRGB hex")] string? Color = null,
    [property: Option("outline", "The outline's colour")] string? Outline = null,
    [property: Option("outline-width", "The outline's width as a fraction of the frame")] double? OutlineWidth = null,
    [property: Option("box", "A box behind each cue, hex with alpha")] string? Box = null,
    [property: Option("shadow", "A soft shadow, hex with alpha")] string? Shadow = null,
    [property: Option("margin", "Distance from the edge as a fraction of the frame")] double? Margin = null,
    [property: Option("max-lines", "The most lines a cue")] int? MaxLines = null,
    [property: Option("max-chars", "The most characters a line")] int? MaxChars = null) : ICommand;
