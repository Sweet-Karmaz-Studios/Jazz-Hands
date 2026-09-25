using JazzHands.Core.Time;

namespace JazzHands.Core.Model;

/// <summary>Where a tracked point was in one frame of its clip.</summary>
/// <param name="Time">When, from the clip's start.</param>
/// <param name="X">Across, in the clip's source pixels.</param>
/// <param name="Y">Down, in the clip's source pixels.</param>
/// <param name="Confidence">How well the frame matched, 0 to 1.</param>
public sealed record TrackPoint(Flicks Time, double X, double Y, double Confidence) : IEquatable<TrackPoint>;

/// <summary>
/// A point of a clip's picture followed through its frames: data on the clip that anything can
/// follow with <c>tracking.apply</c> (a title's position, a callout's target, a mask).
/// </summary>
/// <param name="Id">The track's identifier.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Size">The side of the square of picture followed, in source pixels.</param>
/// <param name="Points">Where the point was, one per frame tracked, in time order.</param>
public sealed record PointTrack(string Id, string Name, int Size, EquatableArray<TrackPoint> Points) : IEquatable<PointTrack>
{
    /// <summary>The point at a time from the clip's start: between two tracked frames it moves in a straight line; outside them it holds.</summary>
    public (double X, double Y)? At(Flicks local)
    {
        if (Points.IsEmpty)
        {
            return null;
        }

        TrackPoint first = Points[0];
        TrackPoint last = Points[^1];
        if (local <= first.Time)
        {
            return (first.X, first.Y);
        }

        if (local >= last.Time)
        {
            return (last.X, last.Y);
        }

        for (int index = 1; index < Points.Length; index++)
        {
            TrackPoint after = Points[index];
            if (after.Time < local)
            {
                continue;
            }

            TrackPoint before = Points[index - 1];
            double span = (after.Time - before.Time).Value;
            double fraction = span <= 0 ? 0 : (local - before.Time).Value / span;
            return (before.X + ((after.X - before.X) * fraction), before.Y + ((after.Y - before.Y) * fraction));
        }

        return (last.X, last.Y);
    }
}
