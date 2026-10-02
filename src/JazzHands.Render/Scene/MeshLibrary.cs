using System.Numerics;
using Serilog;

namespace JazzHands.Render.Scene;

/// <summary>
/// The meshes the 3D scenes draw (Phase 48), made once and kept: text by its look, shapes by their
/// size, models by their file and its last write. The same instance comes back for the same input,
/// which is what the GPU caches key on, so a still text costs nothing after its first frame.
/// Thread safe; the oldest entries go once there are more than a set number.
/// </summary>
public static class MeshLibrary
{
    private const int MostMeshes = 128;
    private const int MostModels = 16;

    private static readonly ILogger LogFor = Log.ForContext(typeof(MeshLibrary));
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (MeshData Mesh, long Used)> Meshes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (GltfModel? Model, string? Error, DateTime Written, long Used)> Models = new(StringComparer.OrdinalIgnoreCase);
    private static long _clock;

    /// <summary>The mesh for a text shape.</summary>
    public static MeshData Text(TextShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return Mesh(shape.Key, () => TextMesh.Build(shape));
    }

    /// <summary>The mesh for a shape.</summary>
    public static MeshData Shape(ShapeKind kind, Vector3 size, int segments, float thickness)
    {
        return Mesh(Shapes.Key(kind, size, segments, thickness), () => Shapes.Make(kind, size, segments, thickness));
    }

    /// <summary>
    /// A model from its file, read again when the file changes. Null with a reason when it cannot
    /// be read; the reason is logged once per change of the file.
    /// </summary>
    public static GltfModel? Model(string path, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        DateTime written = File.Exists(full) ? File.GetLastWriteTimeUtc(full) : DateTime.MinValue;

        lock (Gate)
        {
            if (Models.TryGetValue(full, out var known) && known.Written == written)
            {
                Models[full] = known with { Used = ++_clock };
                error = known.Error;
                return known.Model;
            }
        }

        GltfModel? model = null;
        string? reason = null;
        if (written == DateTime.MinValue)
        {
            reason = $"There is no model at '{full}'.";
        }
        else
        {
            try
            {
                model = Gltf.Read(full);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
            {
                reason = $"'{Path.GetFileName(full)}' could not be read as glTF: {exception.Message}";
            }
        }

        if (reason is not null)
        {
            LogFor.Warning("A 3D model was not drawn: {Reason}", reason);
        }

        lock (Gate)
        {
            Models[full] = (model, reason, written, ++_clock);
            Trim(Models, MostModels);
        }

        error = reason;
        return model;
    }

    private static MeshData Mesh(string key, Func<MeshData> make)
    {
        lock (Gate)
        {
            if (Meshes.TryGetValue(key, out var known))
            {
                Meshes[key] = (known.Mesh, ++_clock);
                return known.Mesh;
            }
        }

        MeshData mesh = make();
        lock (Gate)
        {
            if (Meshes.TryGetValue(key, out var raced))
            {
                return raced.Mesh;
            }

            Meshes[key] = (mesh, ++_clock);
            Trim(Meshes, MostMeshes);
        }

        return mesh;
    }

    private static void Trim<T>(Dictionary<string, T> entries, int most)
    {
        while (entries.Count > most)
        {
            string oldest = entries.MinBy(entry => Used(entry.Value)).Key;
            entries.Remove(oldest);
        }
    }

    private static long Used<T>(T entry) => entry switch
    {
        ValueTuple<MeshData, long> mesh => mesh.Item2,
        ValueTuple<GltfModel?, string?, DateTime, long> model => model.Item4,
        _ => 0,
    };
}
