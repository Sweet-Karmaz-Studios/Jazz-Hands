using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Which way from the frame picked a point is tracked.</summary>
public enum TrackDirection
{
    /// <summary>Both ways, to the clip's start and its end.</summary>
    Both,

    /// <summary>Towards the end.</summary>
    Forward,

    /// <summary>Towards the start.</summary>
    Backward,
}

/// <summary>Follows a point of a clip's picture through its frames, and keeps where it went on the clip.</summary>
/// <remarks>
/// Pick the point at a moment, in the clip's source pixels (as masks are): a corner, a small
/// shape, anything with detail in both directions. The square of <c>--size</c> pixels around it is
/// found again in each frame, forwards and backwards from there, to a fraction of a pixel, and the
/// track stops where the match falls below half (the point went behind something, or out of
/// frame). Naming an existing track with <c>--id</c> re-tracks it from a corrected point: the
/// frames from there onwards (or back) are replaced and the rest kept. Then <c>tracking.apply</c>
/// makes anything follow it. One undo.
/// </remarks>
/// <param name="ClipId">The clip, a clip of a video file.</param>
/// <param name="At">The moment the point is picked, on the sequence.</param>
/// <param name="X">The point across, in the clip's source pixels.</param>
/// <param name="Y">The point down.</param>
/// <param name="Size">The side of the square followed, in source pixels.</param>
/// <param name="Search">How far from where it is expected the point may be found in the next frame.</param>
/// <param name="Direction">both, forward or backward.</param>
/// <param name="TrackId">A track to re-track from here, or the id to give a new one.</param>
/// <param name="Name">What to call a new track.</param>
[Command("tracking.point", Description = "Track a point of a clip's picture through its frames")]
public sealed record TrackPointCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("at", "The moment the point is picked, on the sequence")] Flicks At,
    [property: Option("x", "The point across, in source pixels")] double X,
    [property: Option("y", "The point down, in source pixels")] double Y,
    [property: Option("size", "The square followed, in pixels. Default: 31")] int Size = 31,
    [property: Option("search", "How far it may move in a frame, in pixels. Default: 48")] int Search = 48,
    [property: Option("direction", "both, forward or backward. Default: both")] TrackDirection Direction = TrackDirection.Both,
    [property: Option("id", "A track to re-track from here, or the id for a new one")] string? TrackId = null,
    [property: Option("name", "What to call a new track")] string? Name = null) : ICommand;
