namespace JazzHands.Core.Model;

/// <summary>
/// Where a subtitle sits on the frame, numbered as a keypad is and as ASS's <c>\an</c> tag
/// numbers them: 1 is bottom left, 2 bottom centre, 9 top right.
/// </summary>
public enum SubtitleAlign
{
    /// <summary>Bottom left.</summary>
    BottomLeft = 1,

    /// <summary>Bottom centre, where subtitles go.</summary>
    Bottom = 2,

    /// <summary>Bottom right.</summary>
    BottomRight = 3,

    /// <summary>Middle left.</summary>
    Left = 4,

    /// <summary>The middle of the frame.</summary>
    Middle = 5,

    /// <summary>Middle right.</summary>
    Right = 6,

    /// <summary>Top left.</summary>
    TopLeft = 7,

    /// <summary>Top centre, where a subtitle goes when something is on screen at the bottom.</summary>
    Top = 8,

    /// <summary>Top right.</summary>
    TopRight = 9,
}

/// <summary>
/// How a subtitle track's cues look, as fractions of the frame's height so a style reads the same
/// at any resolution.
/// </summary>
/// <param name="Font">The font family.</param>
/// <param name="Weight">A title weight name: regular, medium, semibold, bold and so on.</param>
/// <param name="Italic">Slanted.</param>
/// <param name="Size">Text height as a fraction of the frame's height: 0.045 is 49 pixels at 1080.</param>
/// <param name="Color">The letters, sRGB hex.</param>
/// <param name="Outline">The outline's colour.</param>
/// <param name="OutlineWidth">The outline's width as a fraction of the frame's height; 0 for none.</param>
/// <param name="Box">A box behind each cue, sRGB hex with alpha; transparent for none.</param>
/// <param name="Shadow">A soft shadow, sRGB hex with alpha; transparent for none.</param>
/// <param name="Margin">How far from the frame's edge the text keeps, as a fraction of its height.</param>
/// <param name="MaxLines">The most lines a cue should have; <c>subtitle.split-long</c> splits longer ones.</param>
/// <param name="MaxChars">The most characters a line should have.</param>
public sealed record SubtitleStyle(
    string Font = "Segoe UI",
    string Weight = "semibold",
    bool Italic = false,
    double Size = 0.045,
    string Color = "#FFFFFF",
    string Outline = "#000000",
    double OutlineWidth = 0.0025,
    string Box = "#00000000",
    string Shadow = "#00000099",
    double Margin = 0.06,
    int MaxLines = 2,
    int MaxChars = 42)
{
    /// <summary>White semibold Segoe UI with a thin black outline and a soft shadow, a line and a half above the bottom.</summary>
    public static SubtitleStyle Default { get; } = new();
}
