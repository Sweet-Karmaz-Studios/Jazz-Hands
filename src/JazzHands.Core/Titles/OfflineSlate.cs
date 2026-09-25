using JazzHands.Core.Model;
using JazzHands.Core.Subtitles;

namespace JazzHands.Core.Titles;

/// <summary>
/// What a clip shows when its file is missing: a red card in the middle of the frame saying so and
/// naming the file, rather than black, which looks like a fade or a bug.
/// </summary>
/// <remarks>
/// The card is the title generator's, so it draws the same in the preview, a rendered frame and an
/// export. An export of a project with missing media is allowed (a rough cut to show someone), and
/// the slate makes the hole plain to whoever watches it.
/// </remarks>
public static class OfflineSlate
{
    /// <summary>The card's look: white on deep red, large, in the middle.</summary>
    public static SubtitleStyle Style { get; } = new(
        Weight: "bold",
        Size: 0.05,
        Color: "#FFFFFF",
        OutlineWidth: 0,
        Box: "#B3261EF2",
        Shadow: "#00000000");

    /// <summary>The title generator's parameters for a missing file's card.</summary>
    /// <param name="name">The media item's name.</param>
    /// <param name="frameWidth">The frame's width.</param>
    /// <param name="frameHeight">The frame's height.</param>
    public static Effect Title(string name, int frameWidth, int frameHeight) =>
        SubtitleLook.Title(Style, $"Media offline\n{name}", SubtitleAlign.Middle, frameWidth, frameHeight);
}
