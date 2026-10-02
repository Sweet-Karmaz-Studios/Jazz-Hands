using System.Globalization;
using System.Numerics;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Render.Scene;

namespace JazzHands.Engine.Handlers;

/// <summary>Puts 3D text on the timeline (Phase 48).</summary>
public sealed class AddText3DHandler : ICommandHandler<AddText3DCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddText3DCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(command.Text))
        {
            throw new CommandException("invalid-value", "3D text needs something to say.", "text");
        }

        string name = command.Name ?? command.Text.Replace("\\n", " ", StringComparison.Ordinal).Trim();
        return SceneHelp.Add(
            project,
            context,
            SceneObjects.Text,
            command.At,
            command.Duration,
            command.TrackId,
            command.SequenceId,
            command.ClipId,
            name.Length > 40 ? name[..40] : name,
            [
                ("text", command.Text),
                ("font", command.Font),
                ("weight", command.Weight?.Trim().ToLowerInvariant()),
                ("size", SceneHelp.Text(command.Size)),
                ("depth", SceneHelp.Text(command.Depth)),
                ("bevel", SceneHelp.Text(command.Bevel)),
                ("color", command.Color),
                ("side-color", command.SideColor),
                ("metallic", SceneHelp.Text(command.Metallic)),
                ("roughness", SceneHelp.Text(command.Roughness)),
            ],
            SceneHelp.Solid);
    }
}

/// <summary>Puts a 3D shape on the timeline (Phase 48).</summary>
public sealed class AddShape3DHandler : ICommandHandler<AddShape3DCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddShape3DCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        string kind = command.Kind.Trim().ToLowerInvariant();
        if (kind is not ("cube" or "sphere" or "cylinder" or "cone" or "torus" or "plane"))
        {
            throw new CommandException("invalid-value", $"'{command.Kind}' is not a shape. There are cube, sphere, cylinder, cone, torus and plane.", "kind");
        }

        return SceneHelp.Add(
            project,
            context,
            SceneObjects.Shape,
            command.At,
            command.Duration,
            command.TrackId,
            command.SequenceId,
            command.ClipId,
            command.Name ?? $"{char.ToUpperInvariant(kind[0])}{kind[1..]}",
            [
                ("kind", kind),
                ("size", command.Size),
                ("depth", SceneHelp.Text(command.Depth)),
                ("color", command.Color),
                ("metallic", SceneHelp.Text(command.Metallic)),
                ("roughness", SceneHelp.Text(command.Roughness)),
            ],
            SceneHelp.Solid);
    }
}

/// <summary>Puts a glTF model on the timeline once it has been read (Phase 48).</summary>
public sealed class AddModel3DHandler : ICommandHandler<AddModel3DCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddModel3DCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        string full = HandlerHelp.Resolve(context, command.File);
        GltfModel model = ModelHelp.Read(full);
        if (model.Meshes.All(mesh => mesh.Triangles == 0))
        {
            throw new CommandException("model-unreadable", $"'{Path.GetFileName(full)}' has no triangles this reader can draw. {string.Join(" ", model.Problems)}".Trim(), "file");
        }

        // An animated model lasts as long as its animation, when nobody says.
        Flicks? duration = command.Duration;
        if (duration is null && ModelHelp.Animation(model, command.Animation) is { } chosen && model.Animations[chosen].Duration > 0.0f)
        {
            duration = Flicks.FromSeconds(model.Animations[chosen].Duration);
        }

        return SceneHelp.Add(
            project,
            context,
            SceneObjects.Model,
            command.At,
            duration,
            command.TrackId,
            command.SequenceId,
            command.ClipId,
            command.Name ?? Path.GetFileNameWithoutExtension(full),
            [
                ("file", HandlerHelp.Store(context, full)),
                ("size", SceneHelp.Text(command.Size)),
                ("animation", command.Animation),
            ],
            SceneHelp.Solid);
    }
}

/// <summary>Says what is in a glTF model (Phase 48).</summary>
public sealed class Model3DInfoHandler : IQueryHandler<Model3DInfoQuery, Model3DInfo>
{
    /// <inheritdoc />
    public Model3DInfo Handle(Project project, Model3DInfoQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        string full = HandlerHelp.Resolve(context.Session?.ProjectPath ?? string.Empty, query.File);
        GltfModel model = ModelHelp.Read(full);
        (Vector3 low, Vector3 high) = model.Bounds();
        PbrMaterial[] materials = [.. model.MeshMaterials.SelectMany(used => used).Distinct()];
        int textures = materials
            .SelectMany(material => new[] { material.BaseColorTexture, material.MetallicRoughnessTexture, material.NormalTexture, material.EmissiveTexture, material.OcclusionTexture })
            .OfType<MeshTexture>()
            .Distinct()
            .Count();

        return new Model3DInfo(
            full,
            [.. model.Meshes.Select((mesh, index) => new Model3DMeshInfo(index, mesh.Triangles, mesh.Parts.Length))],
            materials.Length,
            textures,
            [.. model.Animations.Select((animation, index) => new Model3DAnimationInfo(index, animation.Name, Math.Round(animation.Duration, 3)))],
            [Math.Round(high.X - low.X, 4), Math.Round(high.Y - low.Y, 4), Math.Round(high.Z - low.Z, 4)],
            [.. model.Problems]);
    }
}

/// <summary>Reading a model for a command, its failures as coded refusals.</summary>
internal static class ModelHelp
{
    internal static GltfModel Read(string full)
    {
        if (!File.Exists(full))
        {
            throw new CommandException("file-not-found", $"There is no model at '{full}'.", "file");
        }

        try
        {
            return Gltf.Read(full);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            throw new CommandException("model-unreadable", $"'{Path.GetFileName(full)}' could not be read as glTF 2.0: {exception.Message}", "file");
        }
    }

    /// <summary>Which animation a name or number picks: the first when nothing is said, none when the model has none.</summary>
    internal static int? Animation(GltfModel model, string? chosen)
    {
        if (model.Animations.IsEmpty)
        {
            return null;
        }

        if (int.TryParse(chosen, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number >= 0 && number < model.Animations.Length)
        {
            return number;
        }

        for (int index = 0; index < model.Animations.Length; index++)
        {
            if (string.Equals(model.Animations[index].Name, chosen, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
    }
}
