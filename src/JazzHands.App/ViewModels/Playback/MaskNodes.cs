using System.Globalization;
using System.Numerics;
using System.Text;
using JazzHands.Core.Model;

namespace JazzHands.App.ViewModels.Playback;

/// <summary>One point of a mask's outline, with the tangent handles either side when it is curved.</summary>
/// <param name="Point">The point, in the clip's source pixels.</param>
/// <param name="In">The handle the curve arrives along, or null for a straight edge in.</param>
/// <param name="Out">The handle the curve leaves along, or null for a straight edge out.</param>
public sealed record MaskNode(Vector2 Point, Vector2? In = null, Vector2? Out = null);

/// <summary>
/// A mask's path as points a person can drag, and back to path data: what the preview's mask
/// handles edit.
/// </summary>
/// <remarks>
/// Every figure is closed, as a mask is. A straight edge has no handles; a curved one has the
/// cubic's two controls as the out handle of the point it leaves and the in handle of the one it
/// reaches. Moving a point moves its handles with it, so the curve keeps its shape around it.
/// Written back, straight edges are <c>L</c> and curved ones <c>C</c>, so a polygon stays a
/// polygon and a hand-written path comes back readable.
/// </remarks>
public static class MaskNodes
{
    /// <summary>The figures of a path as points, or empty when it does not read.</summary>
    public static List<List<MaskNode>> Read(string path)
    {
        var figures = new List<List<MaskNode>>();
        List<MaskPathFigure> parsed;
        try
        {
            parsed = MaskPath.Parse(path ?? string.Empty);
        }
        catch (FormatException)
        {
            return figures;
        }

        foreach (MaskPathFigure figure in parsed)
        {
            var nodes = new List<MaskNode> { new(figure.Start) };
            for (int index = 0; index < figure.Segments.Count; index++)
            {
                MaskPathSegment segment = figure.Segments[index];
                bool closing = index == figure.Segments.Count - 1 && segment.End == figure.Start && nodes.Count > 1;
                if (segment.IsCurve)
                {
                    nodes[^1] = nodes[^1] with { Out = Handle(segment.Control1, nodes[^1].Point) };
                }

                if (closing)
                {
                    // The edge back to the start: its handle arrives at the first point.
                    nodes[0] = nodes[0] with { In = segment.IsCurve ? Handle(segment.Control2, figure.Start) : null };
                    break;
                }

                nodes.Add(new MaskNode(segment.End, segment.IsCurve ? Handle(segment.Control2, segment.End) : null));
            }

            figures.Add(nodes);
        }

        return figures;
    }

    /// <summary>Path data for figures of points, each closed.</summary>
    public static string Write(IEnumerable<IReadOnlyList<MaskNode>> figures)
    {
        ArgumentNullException.ThrowIfNull(figures);
        var text = new StringBuilder();
        foreach (IReadOnlyList<MaskNode> nodes in figures)
        {
            if (nodes.Count == 0)
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append("M ").Append(Format(nodes[0].Point));
            for (int index = 0; index < nodes.Count; index++)
            {
                MaskNode from = nodes[index];
                MaskNode to = nodes[(index + 1) % nodes.Count];
                if (from.Out is null && to.In is null)
                {
                    // The last straight edge back to the start is what Z draws.
                    if (index < nodes.Count - 1)
                    {
                        text.Append(" L ").Append(Format(to.Point));
                    }

                    continue;
                }

                text.Append(" C ")
                    .Append(Format(from.Out ?? from.Point)).Append(' ')
                    .Append(Format(to.In ?? to.Point)).Append(' ')
                    .Append(Format(to.Point));
            }

            text.Append(" Z");
        }

        return text.ToString();
    }

    /// <summary>A copy with one point moved by an offset, its handles with it.</summary>
    public static MaskNode Moved(MaskNode node, Vector2 by)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new MaskNode(node.Point + by, node.In + by, node.Out + by);
    }

    /// <summary>
    /// A copy with one handle moved to a place, and the other turned to stay opposite it at its
    /// own length unless <paramref name="broken"/>, which is what makes a smooth point stay smooth.
    /// </summary>
    public static MaskNode WithHandle(MaskNode node, bool outgoing, Vector2 to, bool broken = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        Vector2? other = outgoing ? node.In : node.Out;
        if (!broken && other is { } opposite)
        {
            Vector2 away = node.Point - to;
            float length = Vector2.Distance(opposite, node.Point);
            other = away.LengthSquared() > 1e-6f ? node.Point + (Vector2.Normalize(away) * length) : opposite;
        }

        return outgoing ? node with { Out = to, In = other } : node with { In = to, Out = other };
    }

    /// <summary>
    /// Points along a figure's outline, for drawing it: each curved edge in short straight steps,
    /// each straight one as its two ends. Closed back to the start.
    /// </summary>
    public static List<Vector2> Outline(IReadOnlyList<MaskNode> nodes, int steps = 16)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var points = new List<Vector2>();
        for (int index = 0; index < nodes.Count; index++)
        {
            MaskNode from = nodes[index];
            MaskNode to = nodes[(index + 1) % nodes.Count];
            points.Add(from.Point);
            if (from.Out is null && to.In is null)
            {
                continue;
            }

            Vector2 c1 = from.Out ?? from.Point;
            Vector2 c2 = to.In ?? to.Point;
            for (int step = 1; step < steps; step++)
            {
                float t = step / (float)steps;
                float u = 1 - t;
                points.Add((u * u * u * from.Point) + (3 * u * u * t * c1) + (3 * u * t * t * c2) + (t * t * t * to.Point));
            }
        }

        if (points.Count > 0)
        {
            points.Add(points[0]);
        }

        return points;
    }

    /// <summary>The outline of a rectangle as four points, clockwise from the top left.</summary>
    public static List<MaskNode> Rectangle(Vector4 bounds) =>
    [
        new(new Vector2(bounds.X, bounds.Y)),
        new(new Vector2(bounds.X + bounds.Z, bounds.Y)),
        new(new Vector2(bounds.X + bounds.Z, bounds.Y + bounds.W)),
        new(new Vector2(bounds.X, bounds.Y + bounds.W)),
    ];

    /// <summary>The outline of an ellipse as four smooth points, the usual cubic approximation.</summary>
    public static List<MaskNode> Ellipse(Vector4 bounds)
    {
        const float Kappa = 0.5522848f;
        var centre = new Vector2(bounds.X + (bounds.Z / 2), bounds.Y + (bounds.W / 2));
        var radius = new Vector2(bounds.Z / 2, bounds.W / 2);
        var across = new Vector2(radius.X * Kappa, 0);
        var down = new Vector2(0, radius.Y * Kappa);
        Vector2 top = centre - new Vector2(0, radius.Y);
        Vector2 right = centre + new Vector2(radius.X, 0);
        Vector2 bottom = centre + new Vector2(0, radius.Y);
        Vector2 left = centre - new Vector2(radius.X, 0);
        return
        [
            new(top, top - across, top + across),
            new(right, right - down, right + down),
            new(bottom, bottom + across, bottom - across),
            new(left, left + down, left - down),
        ];
    }

    /// <summary>Bounds as the text <c>param.set bounds</c> takes.</summary>
    public static string BoundsText(Vector4 bounds) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(bounds.X, 1)}, {Math.Round(bounds.Y, 1)}, {Math.Round(bounds.Z, 1)}, {Math.Round(bounds.W, 1)}");

    /// <summary>A control point, or null when it sits on its own point and so is no handle at all.</summary>
    private static Vector2? Handle(Vector2 control, Vector2 point) => Vector2.DistanceSquared(control, point) < 1e-6f ? null : control;

    private static string Format(Vector2 point) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(point.X, 2)} {Math.Round(point.Y, 2)}");
}
