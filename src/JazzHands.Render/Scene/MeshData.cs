using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;

namespace JazzHands.Render.Scene;

/// <summary>One corner of a mesh: where it is, which way it faces, where it reads its textures, and the way its texture's x runs across the surface.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MeshVertex
{
    /// <summary>Where it is, in the mesh's own space.</summary>
    public Vector3 Position;

    /// <summary>The surface's normal, a unit vector.</summary>
    public Vector3 Normal;

    /// <summary>Texture coordinates.</summary>
    public Vector2 Uv;

    /// <summary>The tangent (the way u grows across the surface) in xyz, and in w the handedness of the bitangent, 1 or -1.</summary>
    public Vector4 Tangent;

    /// <summary>A corner with no texture direction yet.</summary>
    public MeshVertex(Vector3 position, Vector3 normal, Vector2 uv)
    {
        Position = position;
        Normal = normal;
        Uv = uv;
        Tangent = new Vector4(1.0f, 0.0f, 0.0f, 1.0f);
    }
}

/// <summary>A run of a mesh's triangles drawn with one material.</summary>
/// <param name="FirstIndex">Its first index.</param>
/// <param name="IndexCount">How many indices, three a triangle.</param>
/// <param name="Material">Which of the mesh's materials.</param>
public readonly record struct MeshPart(int FirstIndex, int IndexCount, int Material);

/// <summary>How a material's alpha is used, as glTF says.</summary>
public enum AlphaMode
{
    /// <summary>Alpha is ignored: fully opaque.</summary>
    Opaque,

    /// <summary>Alpha under the cutoff is not drawn; the rest is opaque.</summary>
    Mask,

    /// <summary>Alpha blends.</summary>
    Blend,
}

/// <summary>
/// A picture a material reads (Phase 48): decoded once, kept as eight bit RGBA, sent to the GPU the
/// first time it is drawn. Colour pictures are sRGB; data pictures (normals, metal and roughness)
/// are linear.
/// </summary>
/// <param name="Key">What it is, for caches: a file and an index, or a hash.</param>
/// <param name="Width">Its width.</param>
/// <param name="Height">Its height.</param>
/// <param name="Rgba">Its texels, row by row, four bytes each.</param>
public sealed record MeshTexture(string Key, int Width, int Height, byte[] Rgba)
{
    /// <inheritdoc />
    public bool Equals(MeshTexture? other) => ReferenceEquals(this, other) || (other is not null && Key == other.Key);

    /// <inheritdoc />
    public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// A physically based material (glTF's metallic and roughness model, Phase 48). Factors multiply
/// their textures; a missing texture is white.
/// </summary>
/// <param name="BaseColor">Base colour, linear, with alpha.</param>
/// <param name="Metallic">0 a dielectric, 1 a metal.</param>
/// <param name="Roughness">0 a mirror, 1 matte.</param>
/// <param name="Emissive">Light it gives out, linear.</param>
public sealed record PbrMaterial(Vector4 BaseColor, float Metallic = 0.0f, float Roughness = 0.5f, Vector3 Emissive = default)
{
    /// <summary>The base colour picture, sRGB.</summary>
    public MeshTexture? BaseColorTexture { get; init; }

    /// <summary>Roughness in green and metal in blue, linear.</summary>
    public MeshTexture? MetallicRoughnessTexture { get; init; }

    /// <summary>A tangent space normal map.</summary>
    public MeshTexture? NormalTexture { get; init; }

    /// <summary>How strongly the normal map bends the surface.</summary>
    public float NormalScale { get; init; } = 1.0f;

    /// <summary>The emissive picture, sRGB.</summary>
    public MeshTexture? EmissiveTexture { get; init; }

    /// <summary>Ambient occlusion in red, linear.</summary>
    public MeshTexture? OcclusionTexture { get; init; }

    /// <summary>Both faces drawn and lit.</summary>
    public bool DoubleSided { get; init; }

    /// <summary>How alpha is used.</summary>
    public AlphaMode Alpha { get; init; } = AlphaMode.Opaque;

    /// <summary>Under this alpha a masked material is not drawn.</summary>
    public float AlphaCutoff { get; init; } = 0.5f;

    /// <summary>Plain grey plastic.</summary>
    public static PbrMaterial Default { get; } = new(new Vector4(0.8f, 0.8f, 0.8f, 1.0f));
}

/// <summary>
/// A mesh's geometry (Phase 48): corners, triangles and the runs of them each material draws, in
/// the mesh's own space. Immutable; the GPU copy is made once per mesh and kept while the mesh is.
/// </summary>
public sealed class MeshData
{
    /// <summary>Makes a mesh, working out its bounds.</summary>
    public MeshData(string key, MeshVertex[] vertices, uint[] indices, ImmutableArray<MeshPart> parts)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);

        Key = key;
        Vertices = vertices;
        Indices = indices;
        Parts = parts.IsDefaultOrEmpty ? [new MeshPart(0, indices.Length, 0)] : parts;

        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        foreach (MeshVertex vertex in vertices)
        {
            low = Vector3.Min(low, vertex.Position);
            high = Vector3.Max(high, vertex.Position);
        }

        Bounds = vertices.Length == 0 ? (Vector3.Zero, Vector3.Zero) : (low, high);
    }

    /// <summary>What it is, for caches and logs.</summary>
    public string Key { get; }

    /// <summary>
    /// For a mesh bent by bones or morph targets (Phase 49a), the mesh at rest it was bent from:
    /// it has the same triangles, and its GPU copy is kept and has its corners rewritten.
    /// </summary>
    public MeshData? Rest { get; init; }

    /// <summary>Its corners.</summary>
    public MeshVertex[] Vertices { get; }

    /// <summary>Its triangles, three indices each, wound as glTF winds them: the cross product of (b - a) and (c - a) points out of the front face.</summary>
    public uint[] Indices { get; }

    /// <summary>The runs of triangles each material draws.</summary>
    public ImmutableArray<MeshPart> Parts { get; }

    /// <summary>The box round it.</summary>
    public (Vector3 Low, Vector3 High) Bounds { get; }

    /// <summary>How many triangles.</summary>
    public int Triangles => Indices.Length / 3;

    /// <summary>
    /// Works out every corner's tangent from the triangles' texture coordinates (Lengyel's method),
    /// for normal maps. Corners whose triangles have no texture stretch get any tangent at right
    /// angles to their normal.
    /// </summary>
    public static void ComputeTangents(MeshVertex[] vertices, uint[] indices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);

        var tangents = new Vector3[vertices.Length];
        var bitangents = new Vector3[vertices.Length];
        for (int at = 0; at + 2 < indices.Length; at += 3)
        {
            uint a = indices[at];
            uint b = indices[at + 1];
            uint c = indices[at + 2];
            Vector3 e1 = vertices[b].Position - vertices[a].Position;
            Vector3 e2 = vertices[c].Position - vertices[a].Position;
            Vector2 d1 = vertices[b].Uv - vertices[a].Uv;
            Vector2 d2 = vertices[c].Uv - vertices[a].Uv;
            float determinant = (d1.X * d2.Y) - (d2.X * d1.Y);
            if (MathF.Abs(determinant) < 1e-12f)
            {
                continue;
            }

            float r = 1.0f / determinant;
            Vector3 tangent = ((e1 * d2.Y) - (e2 * d1.Y)) * r;
            Vector3 bitangent = ((e2 * d1.X) - (e1 * d2.X)) * r;
            foreach (uint corner in (ReadOnlySpan<uint>)[a, b, c])
            {
                tangents[corner] += tangent;
                bitangents[corner] += bitangent;
            }
        }

        for (int index = 0; index < vertices.Length; index++)
        {
            Vector3 n = vertices[index].Normal;
            Vector3 t = tangents[index] - (n * Vector3.Dot(n, tangents[index]));
            if (t.LengthSquared() < 1e-12f)
            {
                t = Vector3.Cross(n, MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
            }

            t = Vector3.Normalize(t);
            float handedness = Vector3.Dot(Vector3.Cross(n, t), bitangents[index]) < 0.0f ? -1.0f : 1.0f;
            vertices[index].Tangent = new Vector4(t, handedness);
        }
    }

    /// <summary>Gives each corner the normal of the faces round it, weighted by their area, for a mesh that came without normals.</summary>
    public static void ComputeNormals(MeshVertex[] vertices, uint[] indices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);

        var sums = new Vector3[vertices.Length];
        for (int at = 0; at + 2 < indices.Length; at += 3)
        {
            uint a = indices[at];
            uint b = indices[at + 1];
            uint c = indices[at + 2];
            Vector3 face = Vector3.Cross(vertices[b].Position - vertices[a].Position, vertices[c].Position - vertices[a].Position);
            sums[a] += face;
            sums[b] += face;
            sums[c] += face;
        }

        for (int index = 0; index < vertices.Length; index++)
        {
            vertices[index].Normal = sums[index].LengthSquared() > 1e-20f ? Vector3.Normalize(sums[index]) : Vector3.UnitZ;
        }
    }
}
