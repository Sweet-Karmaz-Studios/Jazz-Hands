using System.Numerics;
using JazzHands.Core.Animation;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Effects;
using JazzHands.Render.Compositing;
using JazzHands.Render.Scene;

namespace JazzHands.Engine.Handlers;

/// <summary>Where a 3D clip is on the frame through the camera, for the preview's 3D handle (Phase 49a).</summary>
public sealed class Measure3DHandler : IQueryHandler<Measure3DQuery, Layer3DPlaceInfo>
{
    /// <summary>How long a step along an axis is, in sequence pixels at 1080 lines.</summary>
    public const double Step = 100.0;

    /// <inheritdoc />
    public Layer3DPlaceInfo Handle(Project project, Measure3DQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);

        ClipLocation location = HandlerHelp.Clip(project, query.ClipId);
        Clip clip = location.Clip;
        if (location.Track.Kind != TrackKind.Video || (clip.Layer3D is null && !SceneObjects.IsMesh(clip.GeneratorId)))
        {
            throw new CommandException("not-3d", $"'{clip.Name}' is not a 3D layer, text, shape or model. Make a picture 3D with 'jazz clip set-3d'.", "clipId");
        }

        Flicks at = query.At ?? clip.Start + new Flicks(clip.Duration.Value / 2);
        if (at < clip.Start || at >= clip.End)
        {
            throw new CommandException("time-out-of-range", $"'{clip.Name}' plays from {clip.Start} to {clip.End}; {at} is outside it.", "at");
        }

        ProjectSettings settings = project.SettingsFor(location.Sequence);
        var frame = new Vector2(settings.Width, settings.Height);
        Flicks local = at - clip.Start;
        (SceneCamera camera, _) = RenderGraphBuilder.SceneAt(project, location.Sequence, at, new RenderOptions { Effects = EffectCatalog.Registry });
        Vector3 pivot = RenderGraphBuilder.Pivot(project, clip, local, frame);
        float step = (float)(Step * settings.Height / 1080.0);

        FramePoint? Seen(Vector3 point) =>
            camera.Project(point) is { } seen ? new FramePoint(Math.Round(seen.X - (frame.X / 2.0f), 2), Math.Round(seen.Y - (frame.Y / 2.0f), 2)) : null;

        Transform transform = clip.Transform ?? Transform.Identity;
        Layer3D space = clip.Layer3D ?? Layer3D.Default;
        Vector2 position = AnimationEvaluator.Evaluate(transform.Position, local) is ParamValue.Float2 pair ? pair.Value : Vector2.Zero;
        return new Layer3DPlaceInfo(
            clip.Id,
            at,
            Seen(pivot),
            Seen(pivot + new Vector3(step, 0.0f, 0.0f)),
            Seen(pivot + new Vector3(0.0f, step, 0.0f)),
            Seen(pivot + new Vector3(0.0f, 0.0f, step)),
            Math.Round(step, 4),
            Math.Round(camera.Depth(pivot), 2),
            Math.Round(position.X, 2),
            Math.Round(position.Y, 2),
            Number(space.Z, local),
            Number(space.RotationX, local),
            Number(space.RotationY, local),
            Number(transform.Rotation, local));
    }

    private static double Number(AnimatedValue? value, Flicks local) =>
        value is not null && AnimationEvaluator.Evaluate(value, local) is ParamValue.Float number ? Math.Round(number.Value, 3) : 0.0;
}
