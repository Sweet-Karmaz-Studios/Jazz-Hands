using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace JazzHands.Render.Scene;

/// <summary>A node of a glTF model: where it sits under its parent, and the mesh it carries.</summary>
/// <param name="Name">Its name, or empty.</param>
/// <param name="Translation">Move.</param>
/// <param name="Rotation">Turn.</param>
/// <param name="Scale">Scale.</param>
/// <param name="Matrix">A whole matrix in place of the three, or null.</param>
/// <param name="Mesh">The mesh it carries, or -1.</param>
/// <param name="Children">The nodes under it.</param>
public sealed record GltfNode(string Name, Vector3 Translation, Quaternion Rotation, Vector3 Scale, Matrix4x4? Matrix, int Mesh, ImmutableArray<int> Children)
{
    /// <summary>The skin that bends its mesh, or -1 (Phase 49a).</summary>
    public int Skin { get; init; } = -1;

    /// <summary>Its mesh's morph target weights in place of the mesh's own, or null.</summary>
    public float[]? Weights { get; init; }
}

/// <summary>A skin of a glTF model (Phase 49a): the nodes that are its bones, and each one's inverse bind matrix.</summary>
/// <param name="Joints">The bone nodes.</param>
/// <param name="InverseBind">For each bone, the matrix from the model's space into the bone's at rest.</param>
public sealed record GltfSkin(ImmutableArray<int> Joints, Matrix4x4[] InverseBind);

/// <summary>
/// How a mesh bends (Phase 49a): its morph targets (a move of every corner, and of its normal, per
/// target) with their weights at rest, and for a skinned mesh each corner's four bones and how much
/// each pulls. Arrays are a corner each, as the mesh's corners are.
/// </summary>
/// <param name="Positions">For each target, every corner's move.</param>
/// <param name="Normals">For each target, every corner's normal's change.</param>
/// <param name="Weights">Each target's weight at rest.</param>
/// <param name="Joints">For each corner, its four bones as indices into the skin's joints, or null when the mesh has none.</param>
/// <param name="JointWeights">For each corner, how much each of its four bones pulls, or null.</param>
public sealed record GltfDeform(Vector3[][] Positions, Vector3[][] Normals, float[] Weights, Vector4[]? Joints, Vector4[]? JointWeights)
{
    /// <summary>True when the mesh has morph targets.</summary>
    public bool Morphs => Positions.Length > 0;

    /// <summary>True when the mesh has bones to follow.</summary>
    public bool Skinned => Joints is not null && JointWeights is not null;
}

/// <summary>What a glTF animation channel moves.</summary>
public enum GltfPath
{
    /// <summary>A node's move.</summary>
    Translation,

    /// <summary>A node's turn.</summary>
    Rotation,

    /// <summary>A node's scale.</summary>
    Scale,

    /// <summary>A node's mesh's morph target weights, as many numbers as it has targets (Phase 49a).</summary>
    Weights,
}

/// <summary>How a glTF animation channel goes between its keys.</summary>
public enum GltfInterpolation
{
    /// <summary>Straight lines, and spherical for turns.</summary>
    Linear,

    /// <summary>Holds each key until the next.</summary>
    Step,

    /// <summary>Hermite curves through keys with their tangents.</summary>
    CubicSpline,
}

/// <summary>One property of one node moving over time.</summary>
/// <param name="Node">The node.</param>
/// <param name="Path">What of it moves.</param>
/// <param name="Interpolation">How it goes between keys.</param>
/// <param name="Times">Each key's time in seconds.</param>
/// <param name="Values">The keys' values, three or four numbers each (for a cubic spline, in-tangent, value and out-tangent each).</param>
public sealed record GltfChannel(int Node, GltfPath Path, GltfInterpolation Interpolation, float[] Times, float[] Values);

/// <summary>A named animation of a glTF model.</summary>
/// <param name="Name">Its name, or empty.</param>
/// <param name="Channels">What it moves.</param>
public sealed record GltfAnimation(string Name, ImmutableArray<GltfChannel> Channels)
{
    /// <summary>How long it lasts, in seconds: its last key.</summary>
    public float Duration => Channels.IsDefaultOrEmpty ? 0.0f : Channels.Max(channel => channel.Times.Length == 0 ? 0.0f : channel.Times[^1]);
}

/// <summary>
/// A glTF 2.0 model read into meshes, materials, a node tree and animations (Phase 48). Read by
/// <see cref="Gltf.Read"/>; what this reader leaves out (compressed geometry, more than four bones a corner)
/// is listed in <see cref="Problems"/> rather than failing the model.
/// </summary>
/// <param name="Meshes">The meshes, each of every primitive it has, a part per primitive.</param>
/// <param name="MeshMaterials">For each mesh, the material of each of its parts.</param>
/// <param name="Nodes">The nodes.</param>
/// <param name="Roots">The nodes the scene starts from.</param>
/// <param name="Animations">The animations.</param>
/// <param name="Problems">What was left out, in words.</param>
public sealed record GltfModel(
    ImmutableArray<MeshData> Meshes,
    ImmutableArray<ImmutableArray<PbrMaterial>> MeshMaterials,
    ImmutableArray<GltfNode> Nodes,
    ImmutableArray<int> Roots,
    ImmutableArray<GltfAnimation> Animations,
    ImmutableArray<string> Problems)
{
    /// <summary>The skins (Phase 49a).</summary>
    public ImmutableArray<GltfSkin> Skins { get; init; } = [];

    /// <summary>For each mesh, how it bends, or null for one that does not (Phase 49a).</summary>
    public ImmutableArray<GltfDeform?> Deforms { get; init; } = [];

    /// <summary>
    /// Every mesh the scene draws, with its node's matrix in the model's own space (glTF's: y up,
    /// metres), at a time in an animation; at rest when <paramref name="animation"/> is negative.
    /// A mesh with morph targets or a skin is drawn as its corners are then (Phase 49a): targets
    /// blended by their weights, then each corner moved by its bones. A skinned mesh's corners
    /// are in the model's space already, so its matrix is the identity, as glTF has it.
    /// </summary>
    public IReadOnlyList<(int Mesh, Matrix4x4 World, MeshData Drawn)> Pose(int animation, float seconds)
    {
        var moved = new Dictionary<int, (Vector3? T, Quaternion? R, Vector3? S)>();
        var weighted = new Dictionary<int, float[]>();
        if (animation >= 0 && animation < Animations.Length)
        {
            foreach (GltfChannel channel in Animations[animation].Channels)
            {
                if (channel.Path == GltfPath.Weights)
                {
                    int targets = channel.Node >= 0 && channel.Node < Nodes.Length && Nodes[channel.Node].Mesh is var mesh && mesh >= 0 && mesh < Deforms.Length && Deforms[mesh] is { } deform
                        ? deform.Weights.Length
                        : 0;
                    if (targets > 0)
                    {
                        weighted[channel.Node] = Gltf.WeightsAt(channel, seconds, targets);
                    }

                    continue;
                }

                (Vector3? t, Quaternion? r, Vector3? s) = moved.GetValueOrDefault(channel.Node);
                switch (channel.Path)
                {
                    case GltfPath.Translation:
                        t = Gltf.Vector3At(channel, seconds);
                        break;
                    case GltfPath.Rotation:
                        r = Gltf.RotationAt(channel, seconds);
                        break;
                    default:
                        s = Gltf.Vector3At(channel, seconds);
                        break;
                }

                moved[channel.Node] = (t, r, s);
            }
        }

        // Every node's place in the model, which bones need wherever they are in the tree.
        var global = new Matrix4x4?[Nodes.Length];
        var meshNodes = new List<int>();
        var visiting = new HashSet<int>();
        void Visit(int index, Matrix4x4 parent)
        {
            if (index < 0 || index >= Nodes.Length || !visiting.Add(index))
            {
                return;
            }

            GltfNode node = Nodes[index];
            Matrix4x4 local;
            if (node.Matrix is { } fixedMatrix && !moved.ContainsKey(index))
            {
                local = fixedMatrix;
            }
            else
            {
                (Vector3? t, Quaternion? r, Vector3? s) = moved.GetValueOrDefault(index);
                local = Matrix4x4.CreateScale(s ?? node.Scale)
                    * Matrix4x4.CreateFromQuaternion(r ?? node.Rotation)
                    * Matrix4x4.CreateTranslation(t ?? node.Translation);
            }

            Matrix4x4 world = local * parent;
            global[index] = world;
            if (node.Mesh >= 0 && node.Mesh < Meshes.Length)
            {
                meshNodes.Add(index);
            }

            foreach (int child in node.Children)
            {
                Visit(child, world);
            }

            visiting.Remove(index);
        }

        foreach (int root in Roots)
        {
            Visit(root, Matrix4x4.Identity);
        }

        var drawn = new List<(int, Matrix4x4, MeshData)>(meshNodes.Count);
        foreach (int index in meshNodes)
        {
            GltfNode node = Nodes[index];
            MeshData rest = Meshes[node.Mesh];
            if (node.Mesh >= Deforms.Length || Deforms[node.Mesh] is not { } deform)
            {
                drawn.Add((node.Mesh, global[index]!.Value, rest));
                continue;
            }

            float[] weights = weighted.GetValueOrDefault(index) ?? node.Weights ?? deform.Weights;
            Matrix4x4[]? bones = deform.Skinned && node.Skin >= 0 && node.Skin < Skins.Length ? Bones(Skins[node.Skin], global) : null;
            MeshData bent = Gltf.Bend(rest, deform, weights, bones);
            drawn.Add((node.Mesh, bones is null ? global[index]!.Value : Matrix4x4.Identity, bent));
        }

        return drawn;
    }

    /// <summary>Each bone's matrix now: the inverse bind matrix, then where the bone is in the model.</summary>
    private static Matrix4x4[] Bones(GltfSkin skin, Matrix4x4?[] global)
    {
        var bones = new Matrix4x4[skin.Joints.Length];
        for (int joint = 0; joint < bones.Length; joint++)
        {
            int node = skin.Joints[joint];
            Matrix4x4 place = node >= 0 && node < global.Length && global[node] is { } found ? found : Matrix4x4.Identity;
            bones[joint] = (joint < skin.InverseBind.Length ? skin.InverseBind[joint] : Matrix4x4.Identity) * place;
        }

        return bones;
    }

    /// <summary>The box round the model at rest, in its own space.</summary>
    public (Vector3 Low, Vector3 High) Bounds()
    {
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        foreach ((_, Matrix4x4 world, MeshData mesh) in Pose(-1, 0.0f))
        {
            (Vector3 a, Vector3 b) = mesh.Bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                var point = new Vector3((corner & 1) == 0 ? a.X : b.X, (corner & 2) == 0 ? a.Y : b.Y, (corner & 4) == 0 ? a.Z : b.Z);
                Vector3 placed = Vector3.Transform(point, world);
                low = Vector3.Min(low, placed);
                high = Vector3.Max(high, placed);
            }
        }

        return low.X > high.X ? (Vector3.Zero, Vector3.Zero) : (low, high);
    }
}

/// <summary>
/// Reads glTF 2.0 (<c>.gltf</c> with its buffers and pictures beside it or inside as data, and
/// binary <c>.glb</c>), our own reader rather than a package (Phase 48): JSON, buffers, accessors of
/// every component type (normalised or not, strided or not), triangle meshes, metallic and roughness
/// materials, PNG and JPEG textures through WIC, the node tree and node animations.
/// </summary>
public static class Gltf
{
    private const uint GlbMagic = 0x46546C67;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;

    /// <summary>Reads a model file.</summary>
    /// <exception cref="InvalidDataException">The file is not glTF 2.0, or is broken where nothing can be drawn.</exception>
    public static GltfModel Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] file = File.ReadAllBytes(path);
        return Read(file, Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty, Path.GetFullPath(path));
    }

    /// <summary>Reads a model from its bytes; relative buffers and pictures are read from <paramref name="folder"/>.</summary>
    public static GltfModel Read(byte[] file, string folder, string key)
    {
        ArgumentNullException.ThrowIfNull(file);

        byte[] json;
        byte[]? bin = null;
        if (file.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(file) == GlbMagic)
        {
            (json, bin) = Glb(file);
        }
        else
        {
            json = file;
        }

        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return new Reader(document.RootElement, bin, folder, key).Read();
    }

    /// <summary>
    /// The files a model reads beside its own: buffers and pictures named by a relative URI, as
    /// paths relative to the model's folder, each once, in the order the file names them. Embedded
    /// data and absolute URIs are left out. Empty for a file that is not glTF.
    /// </summary>
    public static IReadOnlyList<string> SideFiles(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] file = File.ReadAllBytes(path);
        byte[] json = file.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(file) == GlbMagic ? Glb(file).Json : file;

        var found = new List<string>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (string name in (string[])["buffers", "images"])
            {
                if (!document.RootElement.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement item in array.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("uri", out JsonElement uri) && uri.GetString() is { Length: > 0 } text
                        && !text.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                        && System.Uri.UnescapeDataString(text) is var relative
                        && !Path.IsPathRooted(relative) && !relative.Contains("://", StringComparison.Ordinal)
                        && !found.Contains(relative, StringComparer.OrdinalIgnoreCase))
                    {
                        found.Add(relative);
                    }
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return found;
    }

    /// <summary>A weights channel's values at a time, one per morph target.</summary>
    internal static float[] WeightsAt(GltfChannel channel, float seconds, int targets) => Sample(channel, seconds, targets);

    /// <summary>
    /// A mesh bent (Phase 49a): each target's moves added in at its weight, then, with bones, each
    /// corner carried by its four bones at their weights (normals and tangents turned with them).
    /// A new mesh, which remembers the one it bent so its GPU copy is reused.
    /// </summary>
    internal static MeshData Bend(MeshData rest, GltfDeform deform, float[] weights, Matrix4x4[]? bones)
    {
        MeshVertex[] corners = (MeshVertex[])rest.Vertices.Clone();
        for (int target = 0; target < deform.Positions.Length && target < weights.Length; target++)
        {
            float weight = weights[target];
            if (weight == 0.0f)
            {
                continue;
            }

            Vector3[] moves = deform.Positions[target];
            Vector3[] turns = target < deform.Normals.Length ? deform.Normals[target] : [];
            for (int corner = 0; corner < corners.Length && corner < moves.Length; corner++)
            {
                corners[corner].Position += moves[corner] * weight;
                if (corner < turns.Length)
                {
                    corners[corner].Normal += turns[corner] * weight;
                }
            }
        }

        if (bones is not null && deform.Joints is { } joints && deform.JointWeights is { } pulls)
        {
            for (int corner = 0; corner < corners.Length && corner < joints.Length; corner++)
            {
                Vector4 j = joints[corner];
                Vector4 w = pulls[corner];
                float total = w.X + w.Y + w.Z + w.W;
                if (total <= 0.0f)
                {
                    continue;
                }

                Matrix4x4 skin = (Bone(bones, j.X) * (w.X / total)) + (Bone(bones, j.Y) * (w.Y / total)) + (Bone(bones, j.Z) * (w.Z / total)) + (Bone(bones, j.W) * (w.W / total));
                corners[corner].Position = Vector3.Transform(corners[corner].Position, skin);
                corners[corner].Normal = Vector3.TransformNormal(corners[corner].Normal, skin);
                Vector3 tangent = Vector3.TransformNormal(new Vector3(corners[corner].Tangent.X, corners[corner].Tangent.Y, corners[corner].Tangent.Z), skin);
                corners[corner].Tangent = new Vector4(tangent, corners[corner].Tangent.W);
            }
        }

        for (int corner = 0; corner < corners.Length; corner++)
        {
            if (corners[corner].Normal.LengthSquared() > 1e-12f)
            {
                corners[corner].Normal = Vector3.Normalize(corners[corner].Normal);
            }
        }

        return new MeshData(rest.Key, corners, rest.Indices, rest.Parts) { Rest = rest };

        static Matrix4x4 Bone(Matrix4x4[] bones, float index) =>
            (int)index is var at && at >= 0 && at < bones.Length ? bones[at] : Matrix4x4.Identity;
    }

    /// <summary>A translation or scale channel's value at a time.</summary>
    internal static Vector3 Vector3At(GltfChannel channel, float seconds)
    {
        float[] v = Sample(channel, seconds, 3);
        return new Vector3(v[0], v[1], v[2]);
    }

    /// <summary>A rotation channel's value at a time.</summary>
    internal static Quaternion RotationAt(GltfChannel channel, float seconds)
    {
        float[] v = Sample(channel, seconds, 4);
        var q = new Quaternion(v[0], v[1], v[2], v[3]);
        return q.LengthSquared() > 1e-12f ? Quaternion.Normalize(q) : Quaternion.Identity;
    }

    private static float[] Sample(GltfChannel channel, float seconds, int width)
    {
        float[] times = channel.Times;
        bool cubic = channel.Interpolation == GltfInterpolation.CubicSpline;
        int stride = cubic ? width * 3 : width;
        int offset = cubic ? width : 0;
        float[] Key(int index) => channel.Values.AsSpan((index * stride) + offset, width).ToArray();

        if (times.Length == 0 || channel.Values.Length < stride * times.Length)
        {
            return width == 4 ? [0, 0, 0, 1] : [0, 0, 0];
        }

        if (seconds <= times[0])
        {
            return Key(0);
        }

        if (seconds >= times[^1])
        {
            return Key(times.Length - 1);
        }

        int next = Array.BinarySearch(times, seconds);
        next = next >= 0 ? next + 1 : ~next;
        int previous = Math.Max(0, next - 1);
        next = Math.Min(next, times.Length - 1);
        float span = times[next] - times[previous];
        float t = span > 0 ? (seconds - times[previous]) / span : 0.0f;

        switch (channel.Interpolation)
        {
            case GltfInterpolation.Step:
                return Key(previous);

            case GltfInterpolation.CubicSpline:
            {
                float t2 = t * t;
                float t3 = t2 * t;
                var result = new float[width];
                for (int i = 0; i < width; i++)
                {
                    float p0 = channel.Values[(previous * stride) + width + i];
                    float m0 = channel.Values[(previous * stride) + (2 * width) + i] * span;
                    float p1 = channel.Values[(next * stride) + width + i];
                    float m1 = channel.Values[next * stride + i] * span;
                    result[i] = ((2 * t3) - (3 * t2) + 1) * p0 + (t3 - (2 * t2) + t) * m0 + ((-2 * t3) + (3 * t2)) * p1 + (t3 - t2) * m1;
                }

                return result;
            }

            default:
            {
                float[] a = Key(previous);
                float[] b = Key(next);
                if (width == 4)
                {
                    Quaternion q = Quaternion.Slerp(new Quaternion(a[0], a[1], a[2], a[3]), new Quaternion(b[0], b[1], b[2], b[3]), t);
                    return [q.X, q.Y, q.Z, q.W];
                }

                return [.. a.Select((value, i) => value + ((b[i] - value) * t))];
            }
        }
    }

    private static (byte[] Json, byte[]? Bin) Glb(byte[] file)
    {
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        if (version != 2)
        {
            throw new InvalidDataException($"This is binary glTF version {version}; only version 2 is read.");
        }

        int length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8)), (uint)file.Length);
        byte[]? json = null;
        byte[]? bin = null;
        int at = 12;
        while (at + 8 <= length)
        {
            int chunk = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4));
            if (chunk < 0 || at + 8 + chunk > length)
            {
                throw new InvalidDataException("A chunk of the binary glTF runs past the end of the file.");
            }

            byte[] data = file.AsSpan(at + 8, chunk).ToArray();
            if (type == JsonChunk && json is null)
            {
                json = data;
            }
            else if (type == BinChunk && bin is null)
            {
                bin = data;
            }

            at += 8 + ((chunk + 3) & ~3);
        }

        return (json ?? throw new InvalidDataException("The binary glTF has no JSON chunk."), bin);
    }

    private sealed class Reader(JsonElement root, byte[]? bin, string folder, string key)
    {
        private static readonly HashSet<string> Understood = new(StringComparer.Ordinal) { "KHR_materials_emissive_strength", "KHR_mesh_quantization" };

        private readonly List<string> _problems = [];
        private readonly Dictionary<int, byte[]> _buffers = [];
        private readonly Dictionary<(int Image, bool Srgb), MeshTexture?> _textures = [];

        public GltfModel Read()
        {
            if (root.TryGetProperty("asset", out JsonElement asset) && asset.TryGetProperty("version", out JsonElement version)
                && version.GetString() is { } text && !text.StartsWith('2'))
            {
                throw new InvalidDataException($"This is glTF version {text}; only version 2 is read.");
            }

            foreach (string extension in Strings("extensionsRequired"))
            {
                if (!Understood.Contains(extension))
                {
                    _problems.Add(extension switch
                    {
                        "KHR_draco_mesh_compression" => "Its geometry is Draco compressed, which this reader does not read; meshes that need it are left out.",
                        "KHR_texture_basisu" or "EXT_texture_webp" => $"Its pictures need {extension}, which this reader does not read; those materials are untextured.",
                        _ => $"It needs {extension}, which this reader does not know.",
                    });
                }
            }

            ImmutableArray<PbrMaterial> materials = [.. Array("materials").Select(Material)];
            var meshes = ImmutableArray.CreateBuilder<MeshData>();
            var meshMaterials = ImmutableArray.CreateBuilder<ImmutableArray<PbrMaterial>>();
            var deforms = ImmutableArray.CreateBuilder<GltfDeform?>();
            int meshIndex = 0;
            foreach (JsonElement mesh in Array("meshes"))
            {
                (MeshData data, ImmutableArray<PbrMaterial> used, GltfDeform? deform) = Mesh(mesh, meshIndex++, materials);
                meshes.Add(data);
                meshMaterials.Add(used);
                deforms.Add(deform);
            }

            ImmutableArray<GltfSkin> skins = [.. Array("skins").Select(Skin)];

            ImmutableArray<GltfNode> nodes = [.. Array("nodes").Select(Node)];
            ImmutableArray<int> roots = Roots(nodes);
            ImmutableArray<GltfAnimation> animations = [.. Array("animations").Select(Animation)];

            return new GltfModel(meshes.ToImmutable(), meshMaterials.ToImmutable(), nodes, roots, animations, [.. _problems.Distinct(StringComparer.Ordinal)])
            {
                Skins = skins,
                Deforms = deforms.ToImmutable(),
            };
        }

        private GltfSkin Skin(JsonElement skin)
        {
            ImmutableArray<int> joints = skin.TryGetProperty("joints", out JsonElement list) ? [.. list.EnumerateArray().Select(joint => joint.GetInt32())] : [];
            var inverse = new Matrix4x4[joints.Length];
            System.Array.Fill(inverse, Matrix4x4.Identity);
            if (skin.TryGetProperty("inverseBindMatrices", out JsonElement accessor))
            {
                float[] e = Accessor(accessor.GetInt32(), out int count);
                for (int joint = 0; joint < inverse.Length && joint < count && (joint * 16) + 16 <= e.Length; joint++)
                {
                    int at = joint * 16;
                    inverse[joint] = new Matrix4x4(e[at], e[at + 1], e[at + 2], e[at + 3], e[at + 4], e[at + 5], e[at + 6], e[at + 7], e[at + 8], e[at + 9], e[at + 10], e[at + 11], e[at + 12], e[at + 13], e[at + 14], e[at + 15]);
                }
            }

            return new GltfSkin(joints, inverse);
        }

        private ImmutableArray<int> Roots(ImmutableArray<GltfNode> nodes)
        {
            int scene = root.TryGetProperty("scene", out JsonElement chosen) && chosen.TryGetInt32(out int index) ? index : 0;
            JsonElement[] scenes = [.. Array("scenes")];
            if (scene < scenes.Length && scenes[scene].TryGetProperty("nodes", out JsonElement listed))
            {
                return [.. listed.EnumerateArray().Select(node => node.GetInt32())];
            }

            // No scene: every node that is nobody's child.
            HashSet<int> children = [.. nodes.SelectMany(node => node.Children)];
            return [.. Enumerable.Range(0, nodes.Length).Where(node => !children.Contains(node))];
        }

        private GltfNode Node(JsonElement node)
        {
            Vector3 translation = node.TryGetProperty("translation", out JsonElement t) ? Vector(t, Vector3.Zero) : Vector3.Zero;
            Vector3 scale = node.TryGetProperty("scale", out JsonElement s) ? Vector(s, Vector3.One) : Vector3.One;
            Quaternion rotation = Quaternion.Identity;
            if (node.TryGetProperty("rotation", out JsonElement r) && Floats(r) is { Length: 4 } q)
            {
                rotation = Quaternion.Normalize(new Quaternion(q[0], q[1], q[2], q[3]));
            }

            Matrix4x4? matrix = null;
            if (node.TryGetProperty("matrix", out JsonElement m) && Floats(m) is { Length: 16 } e)
            {
                // Column major in the file; System.Numerics multiplies row vectors, which is the same layout read row by row.
                matrix = new Matrix4x4(e[0], e[1], e[2], e[3], e[4], e[5], e[6], e[7], e[8], e[9], e[10], e[11], e[12], e[13], e[14], e[15]);
            }

            int mesh = node.TryGetProperty("mesh", out JsonElement meshIndex) ? meshIndex.GetInt32() : -1;
            ImmutableArray<int> children = node.TryGetProperty("children", out JsonElement list) ? [.. list.EnumerateArray().Select(child => child.GetInt32())] : [];
            return new GltfNode(Text(node, "name"), translation, rotation, scale, matrix, mesh, children)
            {
                Skin = node.TryGetProperty("skin", out JsonElement skin) ? skin.GetInt32() : -1,
                Weights = node.TryGetProperty("weights", out JsonElement weights) ? Floats(weights) : null,
            };
        }

        private GltfAnimation Animation(JsonElement animation)
        {
            JsonElement[] samplers = [.. Items(animation, "samplers")];
            var channels = ImmutableArray.CreateBuilder<GltfChannel>();
            foreach (JsonElement channel in Items(animation, "channels"))
            {
                if (!channel.TryGetProperty("target", out JsonElement target) || !target.TryGetProperty("node", out JsonElement node)
                    || !channel.TryGetProperty("sampler", out JsonElement samplerIndex) || samplerIndex.GetInt32() >= samplers.Length)
                {
                    continue;
                }

                GltfPath? path = Text(target, "path") switch
                {
                    "translation" => GltfPath.Translation,
                    "rotation" => GltfPath.Rotation,
                    "scale" => GltfPath.Scale,
                    "weights" => GltfPath.Weights,
                    _ => null,
                };
                if (path is null)
                {
                    continue;
                }

                JsonElement sampler = samplers[samplerIndex.GetInt32()];
                GltfInterpolation interpolation = Text(sampler, "interpolation") switch
                {
                    "STEP" => GltfInterpolation.Step,
                    "CUBICSPLINE" => GltfInterpolation.CubicSpline,
                    _ => GltfInterpolation.Linear,
                };

                float[] times = Accessor(sampler.GetProperty("input").GetInt32(), out _);
                float[] values = Accessor(sampler.GetProperty("output").GetInt32(), out _);
                channels.Add(new GltfChannel(node.GetInt32(), path.Value, interpolation, times, values));
            }

            return new GltfAnimation(Text(animation, "name"), channels.ToImmutable());
        }

        private (MeshData Mesh, ImmutableArray<PbrMaterial> Materials, GltfDeform? Deform) Mesh(JsonElement mesh, int index, ImmutableArray<PbrMaterial> materials)
        {
            float[] restWeights = mesh.TryGetProperty("weights", out JsonElement meshWeights) ? Floats(meshWeights) : [];
            var targetMoves = new List<List<Vector3>>();
            var targetTurns = new List<List<Vector3>>();
            var joints = new List<Vector4>();
            var pulls = new List<Vector4>();
            bool skinned = false;
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();
            var parts = ImmutableArray.CreateBuilder<MeshPart>();
            var used = ImmutableArray.CreateBuilder<PbrMaterial>();

            foreach (JsonElement primitive in Items(mesh, "primitives"))
            {
                int mode = primitive.TryGetProperty("mode", out JsonElement m) ? m.GetInt32() : 4;
                if (mode != 4)
                {
                    _problems.Add("It has points, lines or strips, which are left out; only triangle lists are drawn.");
                    continue;
                }

                if (primitive.TryGetProperty("extensions", out JsonElement extensions) && extensions.TryGetProperty("KHR_draco_mesh_compression", out _)
                    && !primitive.GetProperty("attributes").TryGetProperty("POSITION", out _))
                {
                    continue;
                }

                JsonElement attributes = primitive.GetProperty("attributes");
                if (!attributes.TryGetProperty("POSITION", out JsonElement positionAccessor))
                {
                    continue;
                }

                float[] positions = Accessor(positionAccessor.GetInt32(), out int count);
                float[]? normals = attributes.TryGetProperty("NORMAL", out JsonElement n) ? Accessor(n.GetInt32(), out _) : null;
                float[]? uvs = attributes.TryGetProperty("TEXCOORD_0", out JsonElement uv) ? Accessor(uv.GetInt32(), out _) : null;
                float[]? uvs1 = attributes.TryGetProperty("TEXCOORD_1", out JsonElement uv1) ? Accessor(uv1.GetInt32(), out _) : null;
                float[]? tangents = attributes.TryGetProperty("TANGENT", out JsonElement tangent) ? Accessor(tangent.GetInt32(), out _) : null;

                var corners = new MeshVertex[count];
                for (int corner = 0; corner < count; corner++)
                {
                    corners[corner] = new MeshVertex(
                        new Vector3(positions[corner * 3], positions[(corner * 3) + 1], positions[(corner * 3) + 2]),
                        normals is { } nn && nn.Length >= (corner * 3) + 3 ? new Vector3(nn[corner * 3], nn[(corner * 3) + 1], nn[(corner * 3) + 2]) : Vector3.Zero,
                        uvs is { } uu && uu.Length >= (corner * 2) + 2 ? new Vector2(uu[corner * 2], uu[(corner * 2) + 1]) : Vector2.Zero);
                    if (tangents is { } tt && tt.Length >= (corner * 4) + 4)
                    {
                        corners[corner].Tangent = new Vector4(tt[corner * 4], tt[(corner * 4) + 1], tt[(corner * 4) + 2], tt[(corner * 4) + 3]);
                    }

                    if (uvs1 is { } u1 && u1.Length >= (corner * 2) + 2)
                    {
                        corners[corner].Uv1 = new Vector2(u1[corner * 2], u1[(corner * 2) + 1]);
                    }
                }

                uint[] local = primitive.TryGetProperty("indices", out JsonElement indexAccessor)
                    ? Indices(indexAccessor.GetInt32())
                    : [.. Enumerable.Range(0, count).Select(i => (uint)i)];
                local = [.. local.Where(i => i < count)];
                local = local[..(local.Length / 3 * 3)];

                if (normals is null)
                {
                    MeshData.ComputeNormals(corners, local);
                }

                if (tangents is null)
                {
                    MeshData.ComputeTangents(corners, local);
                }

                // Morph targets: each a move of every corner (Phase 49a); a primitive without one moves nothing.
                JsonElement[] targets = primitive.TryGetProperty("targets", out JsonElement targetList) ? [.. targetList.EnumerateArray()] : [];
                while (targetMoves.Count < targets.Length)
                {
                    targetMoves.Add([.. Enumerable.Repeat(Vector3.Zero, vertices.Count)]);
                    targetTurns.Add([.. Enumerable.Repeat(Vector3.Zero, vertices.Count)]);
                }

                for (int target = 0; target < targetMoves.Count; target++)
                {
                    float[]? moves = target < targets.Length && targets[target].TryGetProperty("POSITION", out JsonElement p) ? Accessor(p.GetInt32(), out _) : null;
                    float[]? turns = target < targets.Length && targets[target].TryGetProperty("NORMAL", out JsonElement q) ? Accessor(q.GetInt32(), out _) : null;
                    for (int corner = 0; corner < count; corner++)
                    {
                        targetMoves[target].Add(moves is { } mm && mm.Length >= (corner * 3) + 3 ? new Vector3(mm[corner * 3], mm[(corner * 3) + 1], mm[(corner * 3) + 2]) : Vector3.Zero);
                        targetTurns[target].Add(turns is { } tn && tn.Length >= (corner * 3) + 3 ? new Vector3(tn[corner * 3], tn[(corner * 3) + 1], tn[(corner * 3) + 2]) : Vector3.Zero);
                    }
                }

                // Bones: four per corner and how much each pulls.
                float[]? bones = attributes.TryGetProperty("JOINTS_0", out JsonElement j) ? Accessor(j.GetInt32(), out _, exactIntegers: true) : null;
                float[]? weights = attributes.TryGetProperty("WEIGHTS_0", out JsonElement w) ? Accessor(w.GetInt32(), out _) : null;
                if (attributes.TryGetProperty("JOINTS_1", out _))
                {
                    _problems.Add("Some corners have more than four bones; only the first four are followed.");
                }

                skinned |= bones is not null && weights is not null;
                for (int corner = 0; corner < count; corner++)
                {
                    bool has = bones is { } bb && weights is { } ww && bb.Length >= (corner * 4) + 4 && ww.Length >= (corner * 4) + 4;
                    joints.Add(has ? new Vector4(bones![corner * 4], bones[(corner * 4) + 1], bones[(corner * 4) + 2], bones[(corner * 4) + 3]) : Vector4.Zero);
                    pulls.Add(has ? new Vector4(weights![corner * 4], weights[(corner * 4) + 1], weights[(corner * 4) + 2], weights[(corner * 4) + 3]) : Vector4.Zero);
                }

                uint baseVertex = (uint)vertices.Count;
                parts.Add(new MeshPart(indices.Count, local.Length, used.Count));
                vertices.AddRange(corners);
                indices.AddRange(local.Select(i => i + baseVertex));
                int material = primitive.TryGetProperty("material", out JsonElement chosen) ? chosen.GetInt32() : -1;
                used.Add(material >= 0 && material < materials.Length ? materials[material] : PbrMaterial.Default);
            }

            // Targets the later primitives did not have move nothing there either.
            foreach (List<Vector3> list in targetMoves.Concat(targetTurns))
            {
                list.AddRange(Enumerable.Repeat(Vector3.Zero, vertices.Count - list.Count));
            }

            GltfDeform? deform = targetMoves.Count > 0 || skinned
                ? new GltfDeform(
                    [.. targetMoves.Select(list => list.ToArray())],
                    [.. targetTurns.Select(list => list.ToArray())],
                    [.. Enumerable.Range(0, targetMoves.Count).Select(target => target < restWeights.Length ? restWeights[target] : 0.0f)],
                    skinned ? [.. joints] : null,
                    skinned ? [.. pulls] : null)
                : null;
            return (new MeshData(string.Create(CultureInfo.InvariantCulture, $"gltf:{key}:{index}"), [.. vertices], [.. indices], parts.ToImmutable()), used.ToImmutable(), deform);
        }

        private PbrMaterial Material(JsonElement material)
        {
            Vector4 baseColor = Vector4.One;
            float metallic = 1.0f;
            float roughness = 1.0f;
            MeshTexture? baseTexture = null;
            MeshTexture? metalRough = null;
            if (material.TryGetProperty("pbrMetallicRoughness", out JsonElement pbr))
            {
                if (pbr.TryGetProperty("baseColorFactor", out JsonElement factor) && Floats(factor) is { Length: 4 } f)
                {
                    baseColor = new Vector4(f[0], f[1], f[2], f[3]);
                }

                metallic = pbr.TryGetProperty("metallicFactor", out JsonElement mf) ? mf.GetSingle() : 1.0f;
                roughness = pbr.TryGetProperty("roughnessFactor", out JsonElement rf) ? rf.GetSingle() : 1.0f;
                baseTexture = Texture(pbr, "baseColorTexture", srgb: true);
                metalRough = Texture(pbr, "metallicRoughnessTexture", srgb: false);
            }

            Vector3 emissive = material.TryGetProperty("emissiveFactor", out JsonElement e) ? Vector(e, Vector3.Zero) : Vector3.Zero;
            if (material.TryGetProperty("extensions", out JsonElement extensions)
                && extensions.TryGetProperty("KHR_materials_emissive_strength", out JsonElement strength)
                && strength.TryGetProperty("emissiveStrength", out JsonElement amount))
            {
                emissive *= amount.GetSingle();
            }

            float normalScale = material.TryGetProperty("normalTexture", out JsonElement normal) && normal.TryGetProperty("scale", out JsonElement scale) ? scale.GetSingle() : 1.0f;

            // Which pictures read the second set of texture coordinates, by the shader's map bits.
            int uvSets = (SecondSet(pbr, "baseColorTexture") ? 1 : 0)
                | (SecondSet(pbr, "metallicRoughnessTexture") ? 2 : 0)
                | (SecondSet(material, "normalTexture") ? 4 : 0)
                | (SecondSet(material, "emissiveTexture") ? 8 : 0)
                | (SecondSet(material, "occlusionTexture") ? 16 : 0);
            return new PbrMaterial(baseColor, metallic, roughness, emissive)
            {
                UvSets = uvSets,
                BaseColorTexture = baseTexture,
                MetallicRoughnessTexture = metalRough,
                NormalTexture = Texture(material, "normalTexture", srgb: false),
                NormalScale = normalScale,
                EmissiveTexture = Texture(material, "emissiveTexture", srgb: true),
                OcclusionTexture = Texture(material, "occlusionTexture", srgb: false),
                DoubleSided = material.TryGetProperty("doubleSided", out JsonElement both) && both.GetBoolean(),
                Alpha = Text(material, "alphaMode") switch
                {
                    "MASK" => AlphaMode.Mask,
                    "BLEND" => AlphaMode.Blend,
                    _ => AlphaMode.Opaque,
                },
                AlphaCutoff = material.TryGetProperty("alphaCutoff", out JsonElement cutoff) ? cutoff.GetSingle() : 0.5f,
            };
        }

        /// <summary>True when a material's picture reads texture coordinates other than the first set.</summary>
        private static bool SecondSet(JsonElement owner, string name) =>
            owner.ValueKind == JsonValueKind.Object
            && owner.TryGetProperty(name, out JsonElement info)
            && info.TryGetProperty("texCoord", out JsonElement set)
            && set.GetInt32() >= 1;

        private MeshTexture? Texture(JsonElement owner, string name, bool srgb)
        {
            if (!owner.TryGetProperty(name, out JsonElement info) || !info.TryGetProperty("index", out JsonElement index))
            {
                return null;
            }

            if (info.TryGetProperty("texCoord", out JsonElement set) && set.GetInt32() > 1)
            {
                _problems.Add("A material reads a third or later set of texture coordinates; the second is used.");
            }

            JsonElement[] textures = [.. Array("textures")];
            if (index.GetInt32() >= textures.Length || !textures[index.GetInt32()].TryGetProperty("source", out JsonElement source))
            {
                return null;
            }

            int image = source.GetInt32();
            if (_textures.TryGetValue((image, srgb), out MeshTexture? known))
            {
                return known;
            }

            MeshTexture? decoded = null;
            JsonElement[] images = [.. Array("images")];
            if (image < images.Length && ImageBytes(images[image]) is { } bytes)
            {
                decoded = ImageDecoder.Decode(string.Create(CultureInfo.InvariantCulture, $"{key}#image{image}"), bytes);
                if (decoded is null)
                {
                    _problems.Add($"Picture {image} could not be read; its material is untextured.");
                }
            }

            _textures[(image, srgb)] = decoded;
            return decoded;
        }

        private byte[]? ImageBytes(JsonElement image)
        {
            if (image.TryGetProperty("bufferView", out JsonElement view))
            {
                return View(view.GetInt32()).ToArray();
            }

            return image.TryGetProperty("uri", out JsonElement uri) && uri.GetString() is { } text ? Uri(text) : null;
        }

        private uint[] Indices(int accessor)
        {
            float[] values = Accessor(accessor, out _, exactIntegers: true);
            return [.. values.Select(value => (uint)value)];
        }

        /// <summary>An accessor's elements as floats: every component, normalised where it says so.</summary>
        private float[] Accessor(int index, out int count, bool exactIntegers = false)
        {
            JsonElement accessor = Array("accessors").ElementAt(index);
            count = accessor.GetProperty("count").GetInt32();
            int components = Text(accessor, "type") switch
            {
                "SCALAR" => 1,
                "VEC2" => 2,
                "VEC3" => 3,
                "VEC4" => 4,
                "MAT2" => 4,
                "MAT3" => 9,
                "MAT4" => 16,
                _ => 1,
            };
            int type = accessor.GetProperty("componentType").GetInt32();
            bool normalized = accessor.TryGetProperty("normalized", out JsonElement n) && n.GetBoolean();
            int size = type switch
            {
                5120 or 5121 => 1,
                5122 or 5123 => 2,
                _ => 4,
            };

            var result = new float[count * components];
            if (accessor.TryGetProperty("sparse", out _))
            {
                _problems.Add("It uses sparse accessors, which are read as their base values.");
            }

            if (!accessor.TryGetProperty("bufferView", out JsonElement viewIndex))
            {
                return result;
            }

            JsonElement view = Array("bufferViews").ElementAt(viewIndex.GetInt32());
            ReadOnlySpan<byte> data = View(viewIndex.GetInt32());
            int offset = accessor.TryGetProperty("byteOffset", out JsonElement o) ? o.GetInt32() : 0;
            int stride = view.TryGetProperty("byteStride", out JsonElement s) ? s.GetInt32() : size * components;
            for (int element = 0; element < count; element++)
            {
                for (int component = 0; component < components; component++)
                {
                    int at = offset + (element * stride) + (component * size);
                    if (at + size > data.Length)
                    {
                        throw new InvalidDataException("An accessor reads past the end of its buffer.");
                    }

                    ReadOnlySpan<byte> bytes = data.Slice(at, size);
                    float value = type switch
                    {
                        5120 => normalized && !exactIntegers ? MathF.Max((sbyte)bytes[0] / 127.0f, -1.0f) : (sbyte)bytes[0],
                        5121 => normalized && !exactIntegers ? bytes[0] / 255.0f : bytes[0],
                        5122 => normalized && !exactIntegers ? MathF.Max(BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32767.0f, -1.0f) : BinaryPrimitives.ReadInt16LittleEndian(bytes),
                        5123 => normalized && !exactIntegers ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) / 65535.0f : BinaryPrimitives.ReadUInt16LittleEndian(bytes),
                        5125 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
                        _ => BinaryPrimitives.ReadSingleLittleEndian(bytes),
                    };
                    result[(element * components) + component] = value;
                }
            }

            return result;
        }

        private ReadOnlySpan<byte> View(int index)
        {
            JsonElement view = Array("bufferViews").ElementAt(index);
            byte[] buffer = Buffer(view.GetProperty("buffer").GetInt32());
            int offset = view.TryGetProperty("byteOffset", out JsonElement o) ? o.GetInt32() : 0;
            int length = view.GetProperty("byteLength").GetInt32();
            if (offset < 0 || length < 0 || offset + length > buffer.Length)
            {
                throw new InvalidDataException("A buffer view runs past the end of its buffer.");
            }

            return buffer.AsSpan(offset, length);
        }

        private byte[] Buffer(int index)
        {
            if (_buffers.TryGetValue(index, out byte[]? known))
            {
                return known;
            }

            JsonElement buffer = Array("buffers").ElementAt(index);
            byte[] data = buffer.TryGetProperty("uri", out JsonElement uri) && uri.GetString() is { } text
                ? Uri(text) ?? throw new InvalidDataException($"Buffer {index} ('{text}') could not be read.")
                : bin ?? throw new InvalidDataException($"Buffer {index} has no file and the model has no binary chunk.");
            _buffers[index] = data;
            return data;
        }

        private byte[]? Uri(string uri)
        {
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = uri.IndexOf(',', StringComparison.Ordinal);
                return comma > 0 && uri[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(uri[(comma + 1)..])
                    : null;
            }

            string path = Path.GetFullPath(Path.Combine(folder, System.Uri.UnescapeDataString(uri)));
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        private IEnumerable<JsonElement> Array(string name) =>
            root.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

        private static IEnumerable<JsonElement> Items(JsonElement owner, string name) =>
            owner.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

        private IEnumerable<string> Strings(string name) => Array(name).Select(item => item.GetString() ?? string.Empty);

        private static string Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

        private static float[] Floats(JsonElement array) => [.. array.EnumerateArray().Select(item => item.GetSingle())];

        private static Vector3 Vector(JsonElement array, Vector3 fallback) =>
            Floats(array) is { Length: >= 3 } v ? new Vector3(v[0], v[1], v[2]) : fallback;
    }

    /// <summary>Writes a model as a binary glTF, for tests that need a model of their own: one mesh, its material, an optional texture and animation.</summary>
    internal static byte[] WriteGlb(string json, byte[] bin)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonLength = (jsonBytes.Length + 3) & ~3;
        int binLength = (bin.Length + 3) & ~3;
        int total = 12 + 8 + jsonLength + 8 + binLength;
        byte[] file = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(file, GlbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), (uint)jsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), JsonChunk);
        jsonBytes.CopyTo(file, 20);
        for (int pad = 20 + jsonBytes.Length; pad < 20 + jsonLength; pad++)
        {
            file[pad] = (byte)' ';
        }

        int binAt = 20 + jsonLength;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(binAt), (uint)binLength);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(binAt + 4), BinChunk);
        bin.CopyTo(file, binAt + 8);
        return file;
    }
}
