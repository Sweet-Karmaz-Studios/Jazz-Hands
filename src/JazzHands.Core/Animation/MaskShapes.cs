using System.Globalization;
using System.Numerics;
using System.Text;
using JazzHands.Core.Model;

namespace JazzHands.Core.Animation;

/// <summary>
/// Mask outlines between keyframes: one shape turning into another.
/// </summary>
/// <remarks>
/// <para>
/// Both outlines are read as figures of cubic segments (a straight line is a cubic whose controls
/// sit a third and two thirds along it, so it stays straight). When one figure has fewer segments
/// than the other, its longest segments are split in half until the counts agree, which adds
/// points without changing the shape. Then every point moves in a straight line from one to the
/// other. The outlines must have the same number of figures; when they do not, or either will not
/// read, the shape holds until the next keyframe, as it did before shapes could morph.
/// </para>
/// <para>
/// A figure's start is matched to the other's start, so drawing the second shape from the same
/// corner (as the editor's handles do, by moving the existing points) gives the morph a person
/// expects; a shape drawn from elsewhere turns on the way.
/// </para>
/// </remarks>
public static class MaskShapes
{
    /// <summary>The outline a fraction of the way from one path to another, or null when they cannot morph.</summary>
    public static string? Interpolate(string from, string to, float t)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        List<Vector2[]> a;
        List<Vector2[]> b;
        try
        {
            a = [.. MaskPath.Parse(from).Select(Cubic)];
            b = [.. MaskPath.Parse(to).Select(Cubic)];
        }
        catch (FormatException)
        {
            return null;
        }

        if (a.Count != b.Count || a.Count == 0)
        {
            return null;
        }

        var result = new StringBuilder();
        for (int figure = 0; figure < a.Count; figure++)
        {
            Vector2[] left = a[figure];
            Vector2[] right = b[figure];
            int segments = Math.Max(Segments(left), Segments(right));
            left = Subdivide(left, segments);
            right = Subdivide(right, segments);

            Vector2 Point(int index) => Vector2.Lerp(left[index], right[index], t);
            result.Append('M').Append(Format(Point(0)));
            for (int segment = 0; segment < segments; segment++)
            {
                int at = 1 + (segment * 3);
                result.Append(" C").Append(Format(Point(at))).Append(' ').Append(Format(Point(at + 1))).Append(' ').Append(Format(Point(at + 2)));
            }

            result.Append(" Z ");
        }

        return result.ToString().TrimEnd();
    }

    /// <summary>An outline moved by an offset, written as curves; null when it does not read.</summary>
    public static string? Translate(string path, Vector2 by)
    {
        ArgumentNullException.ThrowIfNull(path);
        List<Vector2[]> figures;
        try
        {
            figures = [.. MaskPath.Parse(path).Select(Cubic)];
        }
        catch (FormatException)
        {
            return null;
        }

        var result = new StringBuilder();
        foreach (Vector2[] points in figures)
        {
            result.Append('M').Append(Format(points[0] + by));
            for (int at = 1; at + 2 < points.Length; at += 3)
            {
                result.Append(" C").Append(Format(points[at] + by)).Append(' ').Append(Format(points[at + 1] + by)).Append(' ').Append(Format(points[at + 2] + by));
            }

            result.Append(" Z ");
        }

        return result.ToString().TrimEnd();
    }

    /// <summary>A figure as cubic points: the start, then three points a segment, closed back to the start.</summary>
    internal static Vector2[] Cubic(MaskPathFigure figure)
    {
        var points = new List<Vector2> { figure.Start };
        Vector2 current = figure.Start;
        IEnumerable<MaskPathSegment> segments = figure.Segments;

        // The closing edge is a segment too, so figures with and without an explicit last point match.
        if (figure.Segments.Count == 0 || figure.Segments[^1].End != figure.Start)
        {
            segments = segments.Append(new MaskPathSegment(figure.Start, false));
        }

        foreach (MaskPathSegment segment in segments)
        {
            if (segment.IsCurve)
            {
                points.Add(segment.Control1);
                points.Add(segment.Control2);
            }
            else
            {
                points.Add(Vector2.Lerp(current, segment.End, 1.0f / 3.0f));
                points.Add(Vector2.Lerp(current, segment.End, 2.0f / 3.0f));
            }

            points.Add(segment.End);
            current = segment.End;
        }

        return [.. points];
    }

    private static int Segments(Vector2[] points) => (points.Length - 1) / 3;

    /// <summary>Splits the longest segments in half (de Casteljau) until there are this many.</summary>
    private static Vector2[] Subdivide(Vector2[] points, int count)
    {
        var list = new List<Vector2>(points);
        while ((list.Count - 1) / 3 < count)
        {
            int longest = 0;
            float length = -1;
            for (int segment = 0; segment < (list.Count - 1) / 3; segment++)
            {
                int at = segment * 3;
                float chord = Vector2.Distance(list[at], list[at + 3]);
                if (chord > length)
                {
                    length = chord;
                    longest = at;
                }
            }

            Vector2 p0 = list[longest];
            Vector2 p1 = list[longest + 1];
            Vector2 p2 = list[longest + 2];
            Vector2 p3 = list[longest + 3];
            Vector2 a = (p0 + p1) / 2;
            Vector2 b = (p1 + p2) / 2;
            Vector2 c = (p2 + p3) / 2;
            Vector2 d = (a + b) / 2;
            Vector2 e = (b + c) / 2;
            Vector2 middle = (d + e) / 2;
            list.RemoveRange(longest + 1, 2);
            list.InsertRange(longest + 1, [a, d, middle, e, c]);
        }

        return [.. list];
    }

    private static string Format(Vector2 point) =>
        string.Create(CultureInfo.InvariantCulture, $"{point.X:0.###},{point.Y:0.###}");
}
