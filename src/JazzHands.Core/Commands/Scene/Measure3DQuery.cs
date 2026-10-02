using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Where a 3D layer, text, shape or model is on the frame through the camera (Phase 49a).</summary>
/// <remarks>
/// Its pivot projected through the camera the sequence is seen through at that moment, and where a
/// step along each of the world's axes lands (right, down and away, a hundred sequence pixels at
/// 1080 lines), with its position, depth and turns then. The preview's 3D handle is drawn from it;
/// dragging along an axis is a change of <c>transform.position</c> or <c>transform.z</c>.
/// </remarks>
/// <param name="ClipId">The clip.</param>
/// <param name="At">When, on the sequence.</param>
[Query("clip.measure-3d", Description = "Where a 3D clip's pivot and axes are on the frame through the camera")]
public sealed record Measure3DQuery(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("at", "When, on the sequence; the clip's middle when left out")] Flicks? At = null) : IQuery<Layer3DPlaceInfo>;
