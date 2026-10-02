using System.Globalization;
using System.Numerics;

namespace JazzHands.Render.Scene;

/// <summary>What a <c>3d.shape</c> is.</summary>
public enum ShapeKind
{
    /// <summary>A box.</summary>
    Cube,

    /// <summary>A ball.</summary>
    Sphere,

    /// <summary>A tube with ends.</summary>
    Cylinder,

    /// <summary>A point over a round base.</summary>
    Cone,

    /// <summary>A ring.</summary>
    Torus,

    /// <summary>A flat rectangle, both faces drawn.</summary>
    Plane,
}

/// <summary>
/// Simple solids as meshes (Phase 48), in sequence pixels about their centre: x right, y down, z
/// away. Every triangle is wound so its cross product points out of the solid, and each surface has
/// texture coordinates running 0 to 1 across it.
/// </summary>
public static class Shapes
{
    /// <summary>A shape by kind, sized by its box: width, height and depth. Round shapes take their radii from it.</summary>
    /// <param name="kind">What shape.</param>
    /// <param name="size">The box it fills.</param>
    /// <param name="segments">Steps round a curve.</param>
    /// <param name="thickness">A torus's tube, as a share of its radius.</param>
    public static MeshData Make(ShapeKind kind, Vector3 size, int segments = 48, float thickness = 0.3f)
    {
        segments = Math.Clamp(segments, 3, 512);
        Vector3 half = Vector3.Abs(size) / 2.0f;
        string key = Key(kind, size, segments, thickness);
        var builder = new Builder();
        switch (kind)
        {
            case ShapeKind.Cube:
                Box(builder, half);
                break;
            case ShapeKind.Sphere:
                Sphere(builder, half, segments);
                break;
            case ShapeKind.Cylinder:
                Lathe(builder, half, segments, top: 1.0f);
                break;
            case ShapeKind.Cone:
                Lathe(builder, half, segments, top: 0.0f);
                break;
            case ShapeKind.Torus:
                Torus(builder, half, segments, Math.Clamp(thickness, 0.01f, 1.0f));
                break;
            default:
                Plane(builder, half);
                break;
        }

        return builder.Build(key);
    }

    /// <summary>What a shape is, for the mesh cache.</summary>
    public static string Key(ShapeKind kind, Vector3 size, int segments, float thickness) =>
        string.Create(CultureInfo.InvariantCulture, $"shape:{kind}:{size.X}:{size.Y}:{size.Z}:{Math.Clamp(segments, 3, 512)}:{thickness}");

    private static void Box(Builder builder, Vector3 half)
    {
        // Each face: its outward normal and the two directions its texture runs along.
        (Vector3 Normal, Vector3 U, Vector3 V)[] faces =
        [
            (-Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY),
            (Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY),
            (Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
            (-Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY),
            (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
            (Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ),
        ];

        foreach ((Vector3 normal, Vector3 u, Vector3 v) in faces)
        {
            Vector3 centre = normal * half;
            Vector3 across = u * half;
            Vector3 down = v * half;
            int first = builder.Count;
            builder.Add(centre - across - down, normal, new Vector2(0, 0));
            builder.Add(centre + across - down, normal, new Vector2(1, 0));
            builder.Add(centre - across + down, normal, new Vector2(0, 1));
            builder.Add(centre + across + down, normal, new Vector2(1, 1));
            builder.Quad(first, first + 1, first + 2, first + 3);
        }
    }

    private static void Plane(Builder builder, Vector3 half)
    {
        int first = builder.Count;
        builder.Add(new Vector3(-half.X, -half.Y, 0), -Vector3.UnitZ, new Vector2(0, 0));
        builder.Add(new Vector3(half.X, -half.Y, 0), -Vector3.UnitZ, new Vector2(1, 0));
        builder.Add(new Vector3(-half.X, half.Y, 0), -Vector3.UnitZ, new Vector2(0, 1));
        builder.Add(new Vector3(half.X, half.Y, 0), -Vector3.UnitZ, new Vector2(1, 1));
        builder.Quad(first, first + 1, first + 2, first + 3);
    }

    private static void Sphere(Builder builder, Vector3 half, int segments)
    {
        int rings = Math.Max(2, segments / 2);
        int first = builder.Count;
        for (int ring = 0; ring <= rings; ring++)
        {
            // From the top (y = -1, up on screen) to the bottom.
            float polar = MathF.PI * ring / rings;
            for (int step = 0; step <= segments; step++)
            {
                float around = 2.0f * MathF.PI * step / segments;
                var unit = new Vector3(MathF.Sin(polar) * MathF.Sin(around), -MathF.Cos(polar), -MathF.Sin(polar) * MathF.Cos(around));
                Vector3 normal = Vector3.Normalize(unit / Vector3.Max(half, new Vector3(1e-6f)));
                builder.Add(unit * half, normal, new Vector2((float)step / segments, (float)ring / rings));
            }
        }

        Grid(builder, first, segments + 1, rings + 1);
    }

    /// <summary>A cylinder (top 1) or a cone (top 0) about the vertical, with its ends closed.</summary>
    private static void Lathe(Builder builder, Vector3 half, int segments, float top)
    {
        int first = builder.Count;
        float slope = half.X * (1.0f - top) / Math.Max(2.0f * half.Y, 1e-6f);
        for (int row = 0; row <= 1; row++)
        {
            float y = row == 0 ? -half.Y : half.Y;
            float scale = row == 0 ? top : 1.0f;
            for (int step = 0; step <= segments; step++)
            {
                float around = 2.0f * MathF.PI * step / segments;
                var radial = new Vector3(MathF.Sin(around), 0.0f, -MathF.Cos(around));
                Vector3 normal = Vector3.Normalize(new Vector3(radial.X, -slope, radial.Z) * new Vector3(1.0f / Math.Max(half.X, 1e-6f), 1.0f, 1.0f / Math.Max(half.Z, 1e-6f)));
                builder.Add(new Vector3(radial.X * half.X * scale, y, radial.Z * half.Z * scale), normal, new Vector2((float)step / segments, row));
            }
        }

        Grid(builder, first, segments + 1, 2);

        Cap(builder, half, segments, y: half.Y, Vector3.UnitY, 1.0f);
        if (top > 0.0f)
        {
            Cap(builder, half, segments, y: -half.Y, -Vector3.UnitY, top);
        }
    }

    private static void Cap(Builder builder, Vector3 half, int segments, float y, Vector3 normal, float scale)
    {
        int centre = builder.Count;
        builder.Add(new Vector3(0, y, 0), normal, new Vector2(0.5f, 0.5f));
        for (int step = 0; step <= segments; step++)
        {
            float around = 2.0f * MathF.PI * step / segments;
            var unit = new Vector2(MathF.Sin(around), -MathF.Cos(around));
            builder.Add(new Vector3(unit.X * half.X * scale, y, unit.Y * half.Z * scale), normal, (unit * 0.5f) + new Vector2(0.5f));
        }

        for (int step = 0; step < segments; step++)
        {
            builder.Triangle(centre, centre + 1 + step, centre + 2 + step);
        }
    }

    private static void Torus(Builder builder, Vector3 half, int segments, float thickness)
    {
        int tube = Math.Max(3, segments / 2);
        int first = builder.Count;
        float minor = thickness / (1.0f + thickness);
        float major = 1.0f - minor;
        for (int ring = 0; ring <= segments; ring++)
        {
            float around = 2.0f * MathF.PI * ring / segments;
            var direction = new Vector3(MathF.Sin(around), 0.0f, -MathF.Cos(around));
            for (int step = 0; step <= tube; step++)
            {
                float turn = 2.0f * MathF.PI * step / tube;
                Vector3 normal = (direction * MathF.Cos(turn)) - (Vector3.UnitY * MathF.Sin(turn));
                Vector3 unit = (direction * major) + (normal * minor);
                Vector3 scaled = Vector3.Normalize(normal / Vector3.Max(new Vector3(half.X, half.X * minor * 2.0f, half.Z), new Vector3(1e-6f)));
                builder.Add(new Vector3(unit.X * half.X, unit.Y * half.X, unit.Z * half.Z), scaled, new Vector2((float)ring / segments, (float)step / tube));
            }
        }

        Grid(builder, first, tube + 1, segments + 1);
    }

    /// <summary>Quads between rows of corners laid out row by row.</summary>
    private static void Grid(Builder builder, int first, int width, int height)
    {
        for (int row = 0; row + 1 < height; row++)
        {
            for (int column = 0; column + 1 < width; column++)
            {
                int a = first + (row * width) + column;
                builder.Quad(a, a + 1, a + width, a + width + 1);
            }
        }
    }

    /// <summary>Corners and triangles as they are made, each triangle turned to face the way its corners' normals do.</summary>
    internal sealed class Builder
    {
        private readonly List<MeshVertex> _vertices = [];
        private readonly List<uint> _indices = [];

        public int Count => _vertices.Count;

        public void Add(Vector3 position, Vector3 normal, Vector2 uv) => _vertices.Add(new MeshVertex(position, normal, uv));

        /// <summary>Two triangles over four corners: top left, top right, bottom left, bottom right.</summary>
        public void Quad(int topLeft, int topRight, int bottomLeft, int bottomRight)
        {
            Triangle(topLeft, topRight, bottomLeft);
            Triangle(topRight, bottomRight, bottomLeft);
        }

        public void Triangle(int a, int b, int c)
        {
            Vector3 pa = _vertices[a].Position;
            Vector3 face = Vector3.Cross(_vertices[b].Position - pa, _vertices[c].Position - pa);
            if (face.LengthSquared() < 1e-12f)
            {
                return;
            }

            Vector3 normal = _vertices[a].Normal + _vertices[b].Normal + _vertices[c].Normal;
            if (Vector3.Dot(face, normal) < 0.0f)
            {
                (b, c) = (c, b);
            }

            _indices.Add((uint)a);
            _indices.Add((uint)b);
            _indices.Add((uint)c);
        }

        public MeshData Build(string key)
        {
            MeshVertex[] vertices = [.. _vertices];
            uint[] indices = [.. _indices];
            MeshData.ComputeTangents(vertices, indices);
            return new MeshData(key, vertices, indices, []);
        }
    }
}
