using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>How <c>vfx.hide-static</c> hides a HUD.</summary>
public enum HideHow
{
    /// <summary>Blur it away inside its regions.</summary>
    Blur,

    /// <summary>Pixelate it inside its regions.</summary>
    Pixelate,

    /// <summary>Cover it with the same place from a frame without it, a clean plate.</summary>
    Fill,

    /// <summary>Crop it off the edge and scale the rest up to fill the frame.</summary>
    Crop,
}

/// <summary>Finds what stands still over a clip's moving picture: a game's HUD, a watermark, a debug line.</summary>
/// <remarks>
/// Frames across the clip are compared: what barely changes and has detail in it (text, icons) is
/// found, as boxes in the clip's source pixels, ready to pass to <c>vfx.hide-static --regions</c>.
/// A number that changes (an FPS counter, a timer) is not found, nor a flat still area.
/// </remarks>
/// <param name="ClipId">The clip, a clip of a video file.</param>
/// <param name="Samples">How many frames across the clip are compared.</param>
[Query("vfx.find-static", Description = "Find what stands still over a clip's moving picture (a game HUD, a watermark, debug text), as regions to hide")]
public sealed record FindStaticQuery(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("samples", "How many frames are compared. Default: 24")] int Samples = 24) : IQuery<StaticRegionInfo[]>;

/// <summary>A region that stands still.</summary>
/// <param name="X">Left, in the clip's source pixels.</param>
/// <param name="Y">Top.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
/// <param name="Score">How much of it is still detail, 0 to 1.</param>
public sealed record StaticRegionInfo(int X, int Y, int Width, int Height, double Score)
{
    /// <summary>As <c>--regions</c> takes it: x,y,width,height.</summary>
    public override string ToString() => FormattableString.Invariant($"{X},{Y},{Width},{Height}");
}

/// <summary>Hides a clip's HUD or watermark: blurred, pixelated, filled from a clean frame, or cropped off.</summary>
/// <remarks>
/// <para>
/// The regions are <c>x,y,width,height</c> in the clip's source pixels, several separated by
/// <c>;</c>; without them, <c>vfx.find-static</c> finds them. <c>blur</c> and <c>pixelate</c>
/// add an effect masked to the regions; <c>fill</c> puts a frozen frame of the same clip from
/// <c>--plate</c> (a moment on the sequence where the HUD is not shown, a menu or a cutscene) on a
/// track above, masked to the regions; <c>crop</c> cuts the regions off the nearest edges and
/// scales the picture up to fill the frame again. One undo.
/// </para>
/// </remarks>
/// <param name="ClipId">The clip.</param>
/// <param name="How">blur, pixelate, fill or crop.</param>
/// <param name="Regions">The regions, or none to find them.</param>
/// <param name="Plate">fill: the moment on the sequence the clean frame is taken from.</param>
[Command("vfx.hide-static", Description = "Hide a clip's HUD or watermark by blur, pixelate, fill from a clean frame, or crop; finds the regions when none are given")]
public sealed record HideStaticCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("how", "blur, pixelate, fill or crop. Default: blur")] HideHow How = HideHow.Blur,
    [property: Option("regions", "x,y,width,height in source pixels, several separated by ;")] string? Regions = null,
    [property: Option("plate", "fill: the moment the clean frame is taken from")] Flicks? Plate = null) : ICommand;
