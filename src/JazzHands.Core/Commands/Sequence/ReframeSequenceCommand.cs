namespace JazzHands.Core.Commands;

/// <summary>How a reframed sequence shows the original.</summary>
public enum ReframeMode
{
    /// <summary>A window cut from the original, filling the frame's width, that can pan and follow a point.</summary>
    Crop,

    /// <summary>The whole original fitted to the width, with the blurred copy above and below.</summary>
    Fit,
}

/// <summary>Makes a vertical (or any other shape) version of a sequence for Shorts, Reels and TikTok.</summary>
/// <remarks>
/// <para>
/// A new sequence the size given nests the original twice: behind, a copy scaled to cover the
/// frame, blurred and darkened; in front, in <c>crop</c> mode, a window cut from the original
/// (<c>--window</c> is its width over its height, 0.8 by default, so there is room above and below
/// for titles; 0.5625 fills a 9:16 frame) filling the frame's width, or in <c>fit</c> mode the
/// whole original across the width. The window is a sequence of its own holding the original,
/// and the original's clip in it is the pan: keyframe its <c>transform.position</c> to move the
/// window, or give <c>--follow</c> a point track (<c>tracking.point</c>) on a clip in the original
/// to keep that point in the middle, smoothed like a camera operator and held inside the picture.
/// </para>
/// <para>
/// Edits to the original show in the vertical version, which is nested, not copied. The new
/// sequence becomes the active one. Export it with <c>youtube-shorts</c>, <c>reels</c> or
/// <c>tiktok</c>. One undo.
/// </para>
/// </remarks>
/// <param name="Size">The new frame, 1080x1920 when not given.</param>
/// <param name="FromSequenceId">The sequence to reframe, the active one when not given.</param>
/// <param name="Mode">crop or fit.</param>
/// <param name="Window">crop: the window's width over its height.</param>
/// <param name="Follow">crop: a point track to keep in the middle of the window.</param>
/// <param name="Blur">The background's blur, in pixels; 0 for none.</param>
/// <param name="Name">What to call the new sequence.</param>
[Command("sequence.reframe", Description = "Make a vertical version of a sequence (for Shorts, Reels, TikTok): a cropped or fitted picture over a blurred copy, the crop panned or following a tracked point")]
public sealed record ReframeSequenceCommand(
    [property: Option("size", "The new frame. Default: 1080x1920")] FrameSize? Size = null,
    [property: Option("from", "The sequence to reframe; the active one when not given")] string? FromSequenceId = null,
    [property: Option("mode", "crop or fit. Default: crop")] ReframeMode Mode = ReframeMode.Crop,
    [property: Option("window", "crop: the window's width over its height. Default: 0.8")] double Window = 0.8,
    [property: Option("follow", "crop: a point track to keep in the middle")] string? Follow = null,
    [property: Option("blur", "The background's blur in pixels. Default: 40")] double Blur = 40,
    [property: Option("name", "What to call the new sequence")] string? Name = null) : ICommand;
