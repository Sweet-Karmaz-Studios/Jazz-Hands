using System.Numerics;
using JazzHands.Core.Animation;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
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
        if (location.Track.Kind != TrackKind.Video || (clip.Layer3D is null && !SceneObjects.IsMesh(clip.GeneratorId) && !SceneObjects.Is(clip.GeneratorId)))
        {
            throw new CommandException("not-3d", $"'{clip.Name}' is not a 3D layer, text, shape, model, camera or light. Make a picture 3D with 'jazz clip set-3d'.", "clipId");
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
        float step = (float)(Step * settings.Height / 1080.0);

        FramePoint? Seen(Vector3 point) =>
            camera.Project(point) is { } seen ? new FramePoint(Math.Round(seen.X - (frame.X / 2.0f), 2), Math.Round(seen.Y - (frame.Y / 2.0f), 2)) : null;

        Layer3DPlaceInfo Placed(Vector3 point, double x, double y, double z, double turnX, double turnY, double turnZ) => new(
            clip.Id,
            at,
            Seen(point),
            Seen(point + new Vector3(step, 0.0f, 0.0f)),
            Seen(point + new Vector3(0.0f, step, 0.0f)),
            Seen(point + new Vector3(0.0f, 0.0f, step)),
            Math.Round(step, 4),
            Math.Round(camera.Depth(point), 2),
            Math.Round(x, 2),
            Math.Round(y, 2),
            Math.Round(z, 2),
            Math.Round(turnX, 3),
            Math.Round(turnY, 3),
            Math.Round(turnZ, 3));

        // A camera is moved by its point of interest, which is in front of it, and turned about it;
        // a light by where it is. Both are their own parameters, not the clip's transform.
        if (SceneObjects.Is(clip.GeneratorId) && EffectCatalog.Registry.Find(clip.GeneratorId!) is { } descriptor)
        {
            Effect? own = clip.Effects.FirstOrDefault(effect => effect.TypeId == clip.GeneratorId);
            ParameterSet p = own is null ? ParameterSet.Defaults(descriptor) : ParameterSet.Evaluate(descriptor, own, local);
            if (clip.GeneratorId == SceneObjects.Camera)
            {
                Vector2 target = p.Float2("target");
                float targetZ = p.Float("target-z");
                return Placed(new Vector3(target, targetZ + p.Float("dolly")), target.X, target.Y, targetZ, p.Float("tilt"), p.Float("orbit"), p.Float("roll")) with
                {
                    PositionParam = "target",
                    DepthParam = "target-z",
                    TurnXParam = "tilt",
                    TurnYParam = "orbit",
                    TurnZParam = "roll",
                };
            }

            Vector2 lamp = p.Float2("position");
            float lampZ = p.Float("position-z");
            return Placed(new Vector3(lamp, lampZ), lamp.X, lamp.Y, lampZ, 0, 0, 0) with
            {
                PositionParam = "position",
                DepthParam = "position-z",
                TurnXParam = null,
                TurnYParam = null,
                TurnZParam = null,
            };
        }

        Vector3 pivot = RenderGraphBuilder.Pivot(project, clip, local, frame);

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
