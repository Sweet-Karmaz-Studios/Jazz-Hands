using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>One title preset.</summary>
/// <param name="Name">The name <c>--preset</c> takes.</param>
/// <param name="Label">What the editor calls it.</param>
/// <param name="Description">When to use it.</param>
/// <param name="Text">The text a title from it gets when none is given.</param>
/// <param name="Duration">How long a title from it lasts when nothing says.</param>
/// <param name="AnimationIn">What it comes in with.</param>
/// <param name="AnimationOut">What it goes out with.</param>
/// <param name="Params">The parameters it sets, as command-line text, for a 1080 line frame.</param>
/// <param name="BuiltIn">True for one that ships with the editor.</param>
/// <param name="Source">The file it came from; empty for a built-in.</param>
public sealed record TitlePresetInfo(
    string Name,
    string Label,
    string Description,
    string Text,
    Flicks Duration,
    string AnimationIn,
    string AnimationOut,
    IReadOnlyDictionary<string, string> Params,
    bool BuiltIn,
    string Source);

/// <summary>One font family a title can use.</summary>
/// <param name="Family">The name the <c>font</c> parameter takes.</param>
/// <param name="Source"><c>project</c> for one in the project's fonts folder, <c>system</c> for one installed.</param>
/// <param name="Faces">How many weights and styles it has.</param>
public sealed record FontInfo(string Family, string Source, int Faces);

/// <summary>A rectangle in sequence pixels from the frame centre.</summary>
/// <param name="X">Its left edge.</param>
/// <param name="Y">Its top edge.</param>
/// <param name="Width">How wide.</param>
/// <param name="Height">How tall.</param>
public sealed record TitleRect(double X, double Y, double Width, double Height);

/// <summary>A point in sequence pixels from the frame centre.</summary>
/// <param name="X">Right of the centre.</param>
/// <param name="Y">Below the centre.</param>
public sealed record TitlePoint(double X, double Y);

/// <summary>Where a title's text sits on the frame.</summary>
/// <param name="ClipId">The title clip.</param>
/// <param name="At">The moment measured, on the sequence.</param>
/// <param name="Block">The text block the alignments place at the position, before any transform.</param>
/// <param name="Text">The lines' own extent, before any transform.</param>
/// <param name="Box">The box, or the text's extent when there is none, before any transform.</param>
/// <param name="Lines">How many lines, wrapping included.</param>
/// <param name="Corners">The box's corners on the picture after the title's offset and zoom and the clip's transform, clockwise from the top left.</param>
/// <param name="InsideTitleSafe">True when every corner is inside title safe, the middle 90% each way.</param>
/// <param name="InsideFrame">True when every corner is on the frame.</param>
public sealed record TitleMeasureInfo(
    string ClipId,
    Flicks At,
    TitleRect Block,
    TitleRect Text,
    TitleRect Box,
    int Lines,
    TitlePoint[] Corners,
    bool InsideTitleSafe,
    bool InsideFrame);
