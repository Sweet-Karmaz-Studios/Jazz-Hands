using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>A point on the frame, in sequence pixels from its centre, x right and y down.</summary>
/// <param name="X">Across.</param>
/// <param name="Y">Down.</param>
public sealed record FramePoint(double X, double Y);

/// <summary>
/// Where a 3D clip is on the frame through the camera at a moment (Phase 49a), and its values
/// then: what the preview's 3D handle is drawn from, and what moving it changes.
/// </summary>
/// <param name="ClipId">The clip.</param>
/// <param name="At">When, on the sequence.</param>
/// <param name="Pivot">Its pivot on the frame, or null when it is behind the camera.</param>
/// <param name="XAxis">Where a step of <paramref name="AxisLength"/> to the right of the pivot lands, or null.</param>
/// <param name="YAxis">Where a step down lands, or null.</param>
/// <param name="ZAxis">Where a step away lands, or null.</param>
/// <param name="AxisLength">How long a step is, in sequence pixels in the world.</param>
/// <param name="Distance">How far in front of the camera the pivot is.</param>
/// <param name="X">Its position across, <c>transform.position</c>.</param>
/// <param name="Y">Its position down.</param>
/// <param name="Z">Its depth, <c>transform.z</c>.</param>
/// <param name="RotationX">Its turn about X, <c>transform.rotation-x</c>.</param>
/// <param name="RotationY">Its turn about Y, <c>transform.rotation-y</c>.</param>
/// <param name="Rotation">Its turn about Z, <c>transform.rotation</c>.</param>
public sealed record Layer3DPlaceInfo(
    string ClipId,
    Flicks At,
    FramePoint? Pivot,
    FramePoint? XAxis,
    FramePoint? YAxis,
    FramePoint? ZAxis,
    double AxisLength,
    double Distance,
    double X,
    double Y,
    double Z,
    double RotationX,
    double RotationY,
    double Rotation);
