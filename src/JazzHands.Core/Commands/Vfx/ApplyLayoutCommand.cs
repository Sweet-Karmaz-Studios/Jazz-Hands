using JazzHands.Core.Model;
namespace JazzHands.Core.Commands;

/// <summary>Places clips on stacked tracks in a layout: a facecam corner, side by side, before and after, grids.</summary>
/// <remarks>
/// <para>
/// Sets each clip's position, scale and crop (and resets its rotation and anchor) so it fills
/// its cell; it does not move clips in time, so put them on tracks over each other first. Layouts
/// (<c>layout.list</c>):
/// </para>
/// <list type="bullet">
/// <item><c>facecam</c>: the first clip fills the frame, the second sits in a corner (<c>--corner</c>,
/// <c>--size</c> as a fraction of the width, <c>--margin</c>) with a <c>video.frame</c>.</item>
/// <item><c>side-by-side</c>: two halves, left and right, each filled and cropped.</item>
/// <item><c>before-after</c>: both fill the frame and the second is cropped from the left at
/// <c>--split</c> (0 to 1), so a keyframed <c>crop.left</c> on it wipes between them.</item>
/// <item><c>grid-2</c> (top and bottom), <c>grid-3</c> (one large, two small) and <c>grid-4</c>
/// (two by two).</item>
/// </list>
/// <para>A gap between cells is <c>--gap</c> pixels. One undo.</para>
/// </remarks>
/// <param name="Layout">The layout.</param>
/// <param name="ClipIds">The clips, in the order the layout fills.</param>
/// <param name="Corner">For facecam: top-left, top-right, bottom-left or bottom-right.</param>
/// <param name="Size">For facecam: its width as a fraction of the frame's.</param>
/// <param name="Margin">For facecam: its distance from the frame's edges, in pixels.</param>
/// <param name="Split">For before-after: where the second clip starts, 0 at the left to 1 at the right.</param>
/// <param name="Gap">Pixels between cells.</param>
[Command("layout.apply", Description = "Lay clips out on the frame: facecam corner, side by side, before and after, grids of 2, 3 and 4")]
public sealed record ApplyLayoutCommand(
    [property: Arg(0, "facecam, side-by-side, before-after, grid-2, grid-3 or grid-4")] string Layout,
    [property: Option("clips", "The clips, in the order the layout fills")] EquatableArray<string> ClipIds,
    [property: Option("corner", "facecam: top-left, top-right, bottom-left or bottom-right. Default: bottom-right")] string Corner = "bottom-right",
    [property: Option("size", "facecam: its width as a fraction of the frame. Default: 0.28")] double Size = 0.28,
    [property: Option("margin", "facecam: distance from the edges, in pixels. Default: 40")] double Margin = 40,
    [property: Option("split", "before-after: where the second clip starts, 0 to 1. Default: 0.5")] double Split = 0.5,
    [property: Option("gap", "Pixels between cells. Default: 0")] double Gap = 0) : ICommand;

/// <summary>The layouts <c>layout.apply</c> knows.</summary>
[Query("layout.list", Description = "The layouts layout.apply knows, with how many clips each takes")]
public sealed record ListLayoutsQuery : IQuery<LayoutInfo[]>;

/// <summary>One layout.</summary>
/// <param name="Name">What to pass to <c>layout.apply</c>.</param>
/// <param name="Clips">How many clips it takes.</param>
/// <param name="Description">What it looks like.</param>
public sealed record LayoutInfo(string Name, int Clips, string Description);
