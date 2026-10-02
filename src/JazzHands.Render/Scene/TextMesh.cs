using System.Globalization;
using System.Numerics;
using JazzHands.Render.Titles;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace JazzHands.Render.Scene;

/// <summary>What 3D text looks like, for building its mesh.</summary>
/// <param name="Text">What it says; a line feed starts a line.</param>
/// <param name="Font">The family.</param>
/// <param name="Weight">How heavy.</param>
/// <param name="Italic">Slanted.</param>
/// <param name="Size">Letter height in sequence pixels.</param>
/// <param name="Align">left, centre or right.</param>
/// <param name="Depth">Front to back.</param>
/// <param name="Bevel">The rounded edge's width.</param>
/// <param name="BevelSegments">Steps round the bevel.</param>
/// <param name="ProjectFolder">Where the project's own fonts are.</param>
public sealed record TextShape(string Text, string Font, FontWeight Weight, bool Italic, float Size, string Align, float Depth, float Bevel, int BevelSegments, string ProjectFolder)
{
    /// <summary>A key for the mesh cache.</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"text:{Text}\u0001{Font}\u0001{(int)Weight}:{Italic}:{Size}:{Align}:{Depth}:{Bevel}:{BevelSegments}:{ProjectFolder}");
}

/// <summary>
/// Builds 3D text (Phase 48): the glyph outlines DirectWrite lays out, flattened and tessellated by
/// Direct2D, then extruded and bevelled into a closed solid. Front and back faces and the bevel are
/// material 0, the sides material 1. Centred on its box; front faces the camera (-z).
/// </summary>
public static class TextMesh
{
    /// <summary>How closely curves are followed, in sequence pixels.</summary>
    public const float Tolerance = 0.1f;

    /// <summary>Corners sharper than this keep their edge on the sides; gentler ones are smoothed.</summary>
    private const float SmoothCos = 0.85f;

    private static readonly Lazy<ID2D1Factory1> Factory = new(() => D2D1.D2D1CreateFactory<ID2D1Factory1>());

    private static readonly object Gate = new();

    /// <summary>Builds the mesh for a text shape; an empty mesh for no text.</summary>
    public static MeshData Build(TextShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        lock (Gate)
        {
            List<Vector2[]> contours = Outline(shape, out ID2D1Geometry? outline);
            using (outline)
            {
                if (outline is null || contours.Count == 0)
                {
                    return new MeshData(shape.Key, [], [], []);
                }

                return Extrude(shape, contours, outline);
            }
        }
    }

    /// <summary>The text's outline as flattened closed contours, and as one geometry for tessellating, centred on its box.</summary>
    private static List<Vector2[]> Outline(TextShape shape, out ID2D1Geometry? outline)
    {
        outline = null;
        if (string.IsNullOrWhiteSpace(shape.Text) || shape.Size <= 0.0f)
        {
            return [];
        }

        IDWriteFactory5 write = FontCatalog.Factory;
        IDWriteFontCollection1 collection = FontCatalog.Collection(shape.ProjectFolder);
        using IDWriteTextFormat format = write.CreateTextFormat(
            shape.Font,
            collection,
            shape.Weight,
            shape.Italic ? FontStyle.Italic : FontStyle.Normal,
            FontStretch.Normal,
            shape.Size,
            "en-us");
        format.TextAlignment = shape.Align switch
        {
            "left" => TextAlignment.Leading,
            "right" => TextAlignment.Trailing,
            _ => TextAlignment.Center,
        };
        format.WordWrapping = WordWrapping.NoWrap;

        string text = shape.Text.Replace("\\n", "\n", StringComparison.Ordinal);
        using IDWriteTextLayout layout = write.CreateTextLayout(text, format, 100000.0f, 100000.0f);
        TextMetrics metrics = layout.Metrics;
        layout.MaxWidth = Math.Max(metrics.WidthIncludingTrailingWhitespace, 1.0f);

        var gatherer = new RunGatherer(Factory.Value);
        try
        {
            layout.Draw(IntPtr.Zero, gatherer, 0.0f, 0.0f);
            if (gatherer.Geometries.Count == 0)
            {
                return [];
            }

            using ID2D1GeometryGroup group = Factory.Value.CreateGeometryGroup(FillMode.Winding, [.. gatherer.Geometries]);
            Rect bounds = group.GetBounds(Matrix3x2.Identity);
            Matrix3x2 centre = Matrix3x2.CreateTranslation(-(bounds.Left + bounds.Right) / 2.0f, -(bounds.Top + bounds.Bottom) / 2.0f);

            // One plain path of every figure, flattened to lines and centred: what is extruded,
            // and, with no bevel, what is tessellated for the faces.
            var flattened = new ContourSink();
            group.Simplify(GeometrySimplificationOption.Lines, centre, Tolerance, flattened);
            outline = Path(flattened.Contours);
            return flattened.Contours;
        }
        finally
        {
            foreach (ID2D1Geometry geometry in gatherer.Geometries)
            {
                geometry.Dispose();
            }
        }
    }

    /// <summary>A path geometry of closed polygons, filled by the nonzero rule.</summary>
    private static ID2D1PathGeometry Path(IReadOnlyList<Vector2[]> contours)
    {
        ID2D1PathGeometry path = Factory.Value.CreatePathGeometry();
        using ID2D1GeometrySink sink = path.Open();
        sink.SetFillMode(FillMode.Winding);
        foreach (Vector2[] contour in contours)
        {
            if (contour.Length < 3)
            {
                continue;
            }

            sink.BeginFigure(contour[0], FigureBegin.Filled);
            sink.AddLines(contour.AsSpan(1).ToArray());
            sink.EndFigure(FigureEnd.Closed);
        }

        sink.Close();
        return path;
    }

    private static MeshData Extrude(TextShape shape, List<Vector2[]> contours, ID2D1Geometry outline)
    {
        float depth = Math.Max(shape.Depth, 0.0f);
        float half = depth / 2.0f;

        // A bevel no wider than a fraction of the letters, nor deeper than half the depth.
        float bevel = Math.Clamp(shape.Bevel, 0.0f, Math.Min(shape.Size * 0.08f, half));
        int segments = bevel > 0.0f ? Math.Clamp(shape.BevelSegments, 1, 16) : 0;

        // Which side of each contour the letter is on: the same side for every contour, as the
        // nonzero rule winds holes against their outlines. Found from the largest contour.
        Vector2[] largest = contours.MaxBy(contour => MathF.Abs(Area(contour)))!;
        bool fillOnLeft = FillIsOnLeft(largest, contours);

        var vertices = new List<MeshVertex>();
        var faces = new List<uint>();
        var sides = new List<uint>();

        // The faces: the outline, or with a bevel the outline drawn in by it.
        List<Vector2[]> inset = bevel > 0.0f ? [.. contours.Select(contour => Offset(contour, -bevel, fillOnLeft))] : contours;
        List<Vector2> triangles;
        if (bevel > 0.0f)
        {
            using ID2D1PathGeometry drawnIn = Path(inset);
            triangles = Tessellate(drawnIn);
        }
        else
        {
            triangles = Tessellate(outline);
        }

        (Vector2 low, Vector2 high) = Box(contours);
        Vector2 extent = Vector2.Max(high - low, new Vector2(1e-3f));
        Face(vertices, faces, triangles, -half, -Vector3.UnitZ, low, extent);
        if (depth > 0.0f)
        {
            Face(vertices, faces, triangles, half, Vector3.UnitZ, low, extent);
        }

        // The bevels and the sides, contour by contour.
        for (int index = 0; index < contours.Count; index++)
        {
            Vector2[] outer = contours[index];
            if (outer.Length < 3 || depth <= 0.0f)
            {
                continue;
            }

            Vector2[] drawn = inset[index];
            Vector2[] normals = EdgeNormals(outer, fillOnLeft);

            // Rings from the front face round the bevel, down the side, round the back bevel.
            // With no bevel the side goes straight from face to face.
            var rings = new List<(float Out, float Z, float Across, float Along)>();
            int sideStart = 0;
            if (segments == 0)
            {
                rings.Add((1.0f, -half, 1.0f, 0.0f));
                rings.Add((1.0f, half, 1.0f, 0.0f));
            }
            else
            {
                for (int step = 0; step <= segments; step++)
                {
                    float theta = MathF.PI / 2.0f * step / segments;
                    rings.Add((MathF.Sin(theta), -half + (bevel * (1.0f - MathF.Cos(theta))), MathF.Sin(theta), -MathF.Cos(theta)));
                }

                sideStart = rings.Count - 1;
                for (int step = segments; step >= 0; step--)
                {
                    float theta = MathF.PI / 2.0f * step / segments;
                    rings.Add((MathF.Sin(theta), half - (bevel * (1.0f - MathF.Cos(theta))), MathF.Sin(theta), MathF.Cos(theta)));
                }
            }

            for (int ring = 0; ring + 1 < rings.Count; ring++)
            {
                bool side = ring == sideStart;
                Band(vertices, side ? sides : faces, outer, drawn, normals, rings[ring], rings[ring + 1], bevel, extent.Y);
            }
        }

        MeshVertex[] all = [.. vertices];
        uint[] indices = [.. faces, .. sides];
        MeshData.ComputeTangents(all, indices);
        return new MeshData(shape.Key, all, indices, [new MeshPart(0, faces.Count, 0), new MeshPart(faces.Count, sides.Count, 1)]);
    }

    /// <summary>A flat face of triangles at a depth, wound to face along its normal.</summary>
    private static void Face(List<MeshVertex> vertices, List<uint> indices, List<Vector2> triangles, float z, Vector3 normal, Vector2 low, Vector2 extent)
    {
        for (int at = 0; at + 2 < triangles.Count; at += 3)
        {
            uint first = (uint)vertices.Count;
            for (int corner = 0; corner < 3; corner++)
            {
                Vector2 p = triangles[at + corner];
                vertices.Add(new MeshVertex(new Vector3(p, z), normal, (p - low) / extent));
            }

            Vector3 a = vertices[(int)first].Position;
            Vector3 face = Vector3.Cross(vertices[(int)first + 1].Position - a, vertices[(int)first + 2].Position - a);
            bool along = Vector3.Dot(face, normal) >= 0.0f;
            indices.Add(first);
            indices.Add(along ? first + 1 : first + 2);
            indices.Add(along ? first + 2 : first + 1);
        }
    }

    /// <summary>
    /// One band round a contour between two rings of its profile: each edge a quad, with corners
    /// gentler than <see cref="SmoothCos"/> smoothed and sharper ones kept as edges.
    /// </summary>
    private static void Band(
        List<MeshVertex> vertices,
        List<uint> indices,
        Vector2[] outer,
        Vector2[] drawn,
        Vector2[] normals,
        (float Out, float Z, float Across, float Along) a,
        (float Out, float Z, float Across, float Along) b,
        float bevel,
        float height)
    {
        int count = outer.Length;
        float travelled = 0.0f;
        for (int edge = 0; edge < count; edge++)
        {
            int next = (edge + 1) % count;
            Vector2 edgeNormal = normals[edge];
            Vector2 startNormal = Smoothed(normals, edge, edge);
            Vector2 endNormal = Smoothed(normals, edge, next);

            Vector2 Point(int corner, float outward) => bevel > 0.0f ? drawn[corner] + ((outer[corner] - drawn[corner]) * outward) : outer[corner];
            Vector3 Normal(Vector2 across, float share, float along) => Vector3.Normalize(new Vector3(across * share, along) + new Vector3(0, 0, 1e-6f));

            float length = Vector2.Distance(outer[edge], outer[next]);
            float u0 = travelled / Math.Max(height, 1e-3f);
            float u1 = (travelled + length) / Math.Max(height, 1e-3f);
            travelled += length;

            uint first = (uint)vertices.Count;
            vertices.Add(new MeshVertex(new Vector3(Point(edge, a.Out), a.Z), Normal(startNormal, a.Across, a.Along), new Vector2(u0, a.Z)));
            vertices.Add(new MeshVertex(new Vector3(Point(next, a.Out), a.Z), Normal(endNormal, a.Across, a.Along), new Vector2(u1, a.Z)));
            vertices.Add(new MeshVertex(new Vector3(Point(edge, b.Out), b.Z), Normal(startNormal, b.Across, b.Along), new Vector2(u0, b.Z)));
            vertices.Add(new MeshVertex(new Vector3(Point(next, b.Out), b.Z), Normal(endNormal, b.Across, b.Along), new Vector2(u1, b.Z)));

            // Wound so the quad faces out of the letter.
            Vector3 outward = new(edgeNormal * Math.Max(a.Across + b.Across, 1e-3f), a.Along + b.Along);
            AddTriangle(vertices, indices, first, first + 1, first + 2, outward);
            AddTriangle(vertices, indices, first + 1, first + 3, first + 2, outward);
        }
    }

    private static void AddTriangle(List<MeshVertex> vertices, List<uint> indices, uint a, uint b, uint c, Vector3 outward)
    {
        Vector3 pa = vertices[(int)a].Position;
        Vector3 face = Vector3.Cross(vertices[(int)b].Position - pa, vertices[(int)c].Position - pa);
        if (face.LengthSquared() < 1e-14f)
        {
            return;
        }

        bool along = Vector3.Dot(face, outward) >= 0.0f;
        indices.Add(a);
        indices.Add(along ? b : c);
        indices.Add(along ? c : b);
    }

    /// <summary>A corner's normal for an edge: smoothed with the edge it meets when the turn is gentle.</summary>
    private static Vector2 Smoothed(Vector2[] normals, int edge, int corner)
    {
        int count = normals.Length;
        int other = corner == edge ? (edge - 1 + count) % count : corner;
        Vector2 mine = normals[edge];
        Vector2 theirs = normals[other];
        return Vector2.Dot(mine, theirs) >= SmoothCos ? Vector2.Normalize(mine + theirs) : mine;
    }

    /// <summary>Each edge's outward normal, pointing away from the letter.</summary>
    private static Vector2[] EdgeNormals(Vector2[] contour, bool fillOnLeft)
    {
        var normals = new Vector2[contour.Length];
        for (int edge = 0; edge < contour.Length; edge++)
        {
            Vector2 direction = contour[(edge + 1) % contour.Length] - contour[edge];
            direction = direction.LengthSquared() > 1e-12f ? Vector2.Normalize(direction) : Vector2.UnitX;

            // Left of the direction, in y-down coordinates, is (y, -x).
            var left = new Vector2(direction.Y, -direction.X);
            normals[edge] = fillOnLeft ? -left : left;
        }

        return normals;
    }

    /// <summary>A contour moved along its outward normals by a distance (negative goes into the letter), mitred and limited at sharp corners.</summary>
    private static Vector2[] Offset(Vector2[] contour, float distance, bool fillOnLeft)
    {
        Vector2[] normals = EdgeNormals(contour, fillOnLeft);
        var moved = new Vector2[contour.Length];
        for (int corner = 0; corner < contour.Length; corner++)
        {
            Vector2 before = normals[(corner - 1 + contour.Length) % contour.Length];
            Vector2 after = normals[corner];
            float join = 1.0f + Vector2.Dot(before, after);
            Vector2 miter = join > 1e-3f ? (before + after) / join : after;
            if (miter.Length() > 2.0f)
            {
                miter = Vector2.Normalize(miter) * 2.0f;
            }

            moved[corner] = contour[corner] + (miter * distance);
        }

        return moved;
    }

    private static bool FillIsOnLeft(Vector2[] contour, IReadOnlyList<Vector2[]> all)
    {
        for (int edge = 0; edge < contour.Length; edge++)
        {
            Vector2 a = contour[edge];
            Vector2 b = contour[(edge + 1) % contour.Length];
            Vector2 direction = b - a;
            if (direction.LengthSquared() < 1e-6f)
            {
                continue;
            }

            Vector2 left = Vector2.Normalize(new Vector2(direction.Y, -direction.X));
            Vector2 probe = ((a + b) / 2.0f) + (left * 0.05f);
            return Winding(probe, all) != 0;
        }

        return true;
    }

    /// <summary>The nonzero winding number of a point over every contour.</summary>
    private static int Winding(Vector2 point, IReadOnlyList<Vector2[]> all)
    {
        int winding = 0;
        foreach (Vector2[] contour in all)
        {
            for (int edge = 0; edge < contour.Length; edge++)
            {
                Vector2 a = contour[edge];
                Vector2 b = contour[(edge + 1) % contour.Length];
                float cross = ((b.X - a.X) * (point.Y - a.Y)) - ((point.X - a.X) * (b.Y - a.Y));
                if (a.Y <= point.Y)
                {
                    if (b.Y > point.Y && cross > 0)
                    {
                        winding++;
                    }
                }
                else if (b.Y <= point.Y && cross < 0)
                {
                    winding--;
                }
            }
        }

        return winding;
    }

    private static float Area(Vector2[] contour)
    {
        float sum = 0.0f;
        for (int edge = 0; edge < contour.Length; edge++)
        {
            Vector2 a = contour[edge];
            Vector2 b = contour[(edge + 1) % contour.Length];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return sum / 2.0f;
    }

    private static (Vector2 Low, Vector2 High) Box(IEnumerable<Vector2[]> contours)
    {
        var low = new Vector2(float.MaxValue);
        var high = new Vector2(float.MinValue);
        foreach (Vector2 point in contours.SelectMany(contour => contour))
        {
            low = Vector2.Min(low, point);
            high = Vector2.Max(high, point);
        }

        return (low, high);
    }

    private static List<Vector2> Tessellate(ID2D1Geometry geometry)
    {
        var sink = new TriangleSink();
        geometry.Tessellate(Matrix3x2.Identity, Tolerance, sink);
        return sink.Points;
    }

    /// <summary>Collects each glyph run's outline, moved to where the layout put it.</summary>
    private sealed class RunGatherer(ID2D1Factory1 factory) : TextRendererBase
    {
        public List<ID2D1Geometry> Geometries { get; } = [];

        public override void DrawGlyphRun(IntPtr clientDrawingContext, float baselineOriginX, float baselineOriginY, MeasuringMode measuringMode, GlyphRun glyphRun, GlyphRunDescription glyphRunDescription, IUnknown clientDrawingEffect)
        {
            if (glyphRun.FontFace is null || glyphRun.Indices is null || glyphRun.Indices.Length == 0)
            {
                return;
            }

            using ID2D1PathGeometry geometry = factory.CreatePathGeometry();
            using (ID2D1GeometrySink sink = geometry.Open())
            {
                glyphRun.FontFace.GetGlyphRunOutline(
                    glyphRun.FontEmSize,
                    glyphRun.Indices,
                    glyphRun.Advances,
                    glyphRun.Offsets,
                    glyphRun.IsSideways,
                    glyphRun.BidiLevel % 2 == 1,
                    sink);
                sink.Close();
            }

            Geometries.Add(factory.CreateTransformedGeometry(geometry, Matrix3x2.CreateTranslation(baselineOriginX, baselineOriginY)));
        }
    }

    /// <summary>Takes a flattened geometry's figures as closed polygons.</summary>
    private sealed class ContourSink : CallbackBase, ID2D1SimplifiedGeometrySink
    {
        private List<Vector2>? _current;

        public List<Vector2[]> Contours { get; } = [];

        public void SetFillMode(FillMode fillMode)
        {
        }

        public void SetSegmentFlags(PathSegment vertexFlags)
        {
        }

        public void BeginFigure(Vector2 startPoint, FigureBegin figureBegin) => _current = [startPoint];

        public void AddLines(Vector2[] points) => _current?.AddRange(points);

        public void AddBeziers(BezierSegment[] beziers)
        {
            // Simplified to lines, so there are none; take their end points if there ever are.
            foreach (BezierSegment bezier in beziers)
            {
                _current?.Add(bezier.Point3);
            }
        }

        public void EndFigure(FigureEnd figureEnd)
        {
            if (_current is { Count: >= 3 } points)
            {
                // A closed figure may repeat its first point at the end.
                if (Vector2.DistanceSquared(points[0], points[^1]) < 1e-10f)
                {
                    points.RemoveAt(points.Count - 1);
                }

                if (points.Count >= 3)
                {
                    Contours.Add([.. points]);
                }
            }

            _current = null;
        }

        public void Close()
        {
        }
    }

    /// <summary>Takes a tessellation's triangles, three points each.</summary>
    private sealed class TriangleSink : CallbackBase, ID2D1TessellationSink
    {
        public List<Vector2> Points { get; } = [];

        public void AddTriangles(Triangle[] triangles)
        {
            foreach (Triangle triangle in triangles)
            {
                Points.Add(triangle.Point1);
                Points.Add(triangle.Point2);
                Points.Add(triangle.Point3);
            }
        }

        public void Close()
        {
        }
    }
}
