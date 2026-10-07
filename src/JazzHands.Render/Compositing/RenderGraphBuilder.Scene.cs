using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Core.Drivers;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Queries;
using JazzHands.Core.Time;
using JazzHands.Render.Color;
using JazzHands.Render.Effects.Scene;
using JazzHands.Render.Scene;

namespace JazzHands.Render.Compositing;

/// <summary>The 3D layers (Phase 47): scenes, canvases, the camera and the lights.</summary>
public static partial class RenderGraphBuilder
{
    /// <summary>The most a canvas may be across, in texels.</summary>
    private const int MostCanvas = 8192;

    /// <summary>Transparent texels round every canvas, so a quad's edge is the picture's own, filtered, rather than the rasterizer's stair.</summary>
    private const int CanvasMargin = 2;

    /// <summary>
    /// The camera a sequence is seen through at a time, and the lights: the topmost enabled camera
    /// clip under the time (the default camera when there is none) and every enabled light clip,
    /// topmost first.
    /// </summary>
    public static (SceneCamera Camera, ImmutableArray<SceneLight> Lights) SceneAt(Project project, Sequence sequence, Flicks time, RenderOptions options, IFrameProvider? frames = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(options);

        // Reading a model's own parameters below moves the drivers' origin; it is put back after.
        Flicks origin = DriverScope.Origin;
        ProjectSettings settings = project.SettingsFor(sequence);
        var frameSize = new Vector2(settings.Width, settings.Height);
        HashSet<string> mattes = TrackMatte.Sources(sequence);

        Clip? camera = null;
        var lights = new List<Clip>();
        foreach (Track track in sequence.Tracks)
        {
            if (track.Kind != TrackKind.Video || track.Muted || mattes.Contains(track.Id))
            {
                continue;
            }

            if (TimelineQueries.ClipAt(track, time) is not { Enabled: true } clip)
            {
                continue;
            }

            switch (clip.GeneratorId)
            {
                case SceneObjects.Camera:
                    camera = clip;
                    break;
                case SceneObjects.Light:
                    lights.Add(clip);
                    break;

                // A model may be looked through by one of its own cameras, and light with its own lights.
                case SceneObjects.Model when OwnParameters(clip, SceneObjects.Model, time, options) is { } model:
                    if (model.Text("camera").Trim().Length > 0)
                    {
                        camera = clip;
                    }

                    if (model.Bool("lights"))
                    {
                        lights.Add(clip);
                    }

                    break;
            }
        }

        lights.Reverse();
        try
        {
            SceneCamera seen = camera is null ? DefaultCamera(frameSize)
                : camera.GeneratorId == SceneObjects.Model ? ModelCamera(camera, time, frameSize, options) ?? DefaultCamera(frameSize)
                : CameraOf(camera, time, frameSize, options);
            return (seen, [.. lights.SelectMany(light => light.GeneratorId == SceneObjects.Model
                ? ModelLights(light, time, options)
                : LightOf(project, light, time, options, frames) is { } one ? [one] : Array.Empty<SceneLight>())]);
        }
        finally
        {
            DriverScope.Origin = origin;
        }
    }

    /// <summary>The camera at rest, which sees the frame exactly as it is.</summary>
    public static SceneCamera DefaultCamera(Vector2 frameSize)
    {
        (Vector3 position, Vector3 right, Vector3 down, Vector3 forward, float zoom) = SceneMath.Camera(
            frameSize, Vector2.Zero, 0.0f, Vector3.Zero, 0.0f, 0.0f, 0.0f, CameraGenerator.RestAngle, CameraGenerator.RestAngle);
        return new SceneCamera(position, right, down, forward, zoom, frameSize);
    }

    /// <summary>
    /// Where a 3D clip's pivot is in the world at a time (Phase 49a): its anchor, carried by its
    /// placement, which its turns are about and its position and depth move. For a picture the
    /// anchor is fitted to the frame as the picture is; text, shapes and models are not fitted.
    /// </summary>
    public static Vector3 Pivot(Project project, Clip clip, Flicks local, Vector2 frameSize)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(clip);

        Vector2 fit = Vector2.One;
        if (!SceneObjects.IsMesh(clip.GeneratorId)
            && clip.MediaId is { } mediaId
            && project.MediaItem(mediaId) is { } item
            && item.Info?.Streams.FirstOrDefault(stream => stream.Index == clip.SourceStreamIndex) is { Width: > 0, Height: > 0 } stream)
        {
            fit = FitScale(new Vector2(stream.Width, stream.Height), frameSize, item.Conform);
        }

        Vector2 anchor = Float2((clip.Transform ?? Transform.Identity).Anchor, Intrinsic.Anchor, local) * fit;
        return Vector3.Transform(new Vector3(anchor, 0.0f), LayerPlacement(clip, local, fit));
    }

    /// <summary>A 3D clip's place in the world at a time: from its picture's own space (sequence pixels from the fitted picture's centre, unscaled) to the world.</summary>
    public static Matrix4x4 LayerPlacement(Clip clip, Flicks local, Vector2 fit)
    {
        ArgumentNullException.ThrowIfNull(clip);

        Transform transform = clip.Transform ?? Transform.Identity;
        Layer3D space = clip.Layer3D ?? Layer3D.Default;
        return SceneMath.LayerToWorld(
            Float2(transform.Anchor, Intrinsic.Anchor, local) * fit,
            Float2(transform.Scale, Intrinsic.Scale, local),
            Float(space.RotationX, Intrinsic.RotationX, local),
            Float(space.RotationY, Intrinsic.RotationY, local),
            Float(transform.Rotation, Intrinsic.Rotation, local),
            Float2(transform.Position, Intrinsic.Position, local),
            Float(space.Z, Intrinsic.Depth, local));
    }

    /// <summary>A 3D clip in a scene being built: its layer at the frame, and what places it at another moment.</summary>
    /// <param name="Track">Its track.</param>
    /// <param name="Clip">The clip.</param>
    /// <param name="Layer">Its layer at the frame.</param>
    /// <param name="CanvasToPicture">Canvas pixels to its picture's own space, which <see cref="LayerPlacement"/> then sets in the world.</param>
    /// <param name="Fit">How its picture was fitted to the frame.</param>
    private sealed record ScenePart(Track Track, Clip Clip, SceneLayer? Layer, Matrix4x4 CanvasToPicture, Vector2 Fit)
    {
        /// <summary>For text, a shape or a model: its meshes at the frame.</summary>
        public ImmutableArray<SceneMesh> Meshes { get; init; } = [];

        /// <summary>For text, a shape or a model: its meshes at another moment.</summary>
        public Func<Flicks, ImmutableArray<SceneMesh>>? MeshesAt { get; init; }

        /// <summary>Every corner it has in the world at a moment, for how far it moves.</summary>
        public IEnumerable<Vector3> Corners(Flicks time) =>
            Layer is not null ? At(time)!.Corners() : (MeshesAt?.Invoke(time) ?? Meshes).SelectMany(mesh => mesh.Corners());

        /// <summary>The layer at another moment: placed, faded and lit as it is then, with the same canvas.</summary>
        public SceneLayer? At(Flicks time)
        {
            if (Layer is null)
            {
                return null;
            }

            Flicks local = time - Clip.Start;
            return Layer with
            {
                World = CanvasToPicture * LayerPlacement(Clip, local, Fit),
                Opacity = Float(Clip.Opacity, Intrinsic.Opacity, local),
                Material = MaterialOf(Clip, local),
            };
        }
    }

    /// <summary>
    /// A run of 3D clips as one layer of the stack: the scene at the frame, or, when motion blur is
    /// on for its camera or any of its layers and something in it moves, the scene at moments
    /// across the shutter, averaged. Each moment re-places the canvases and the camera and lights;
    /// the canvases' pictures are the frame's, as a flat layer's are under motion blur.
    /// </summary>
    private static LayerNode SceneNode(
        Project project,
        Sequence sequence,
        Flicks time,
        IFrameProvider frames,
        IReadOnlyList<ScenePart> parts,
        (SceneCamera Camera, ImmutableArray<SceneLight> Lights) setup,
        (int Width, int Height) output,
        RenderOptions options,
        Rational frameRate)
    {
        var still = new LayerNode(
            new SceneLayerSource([.. parts.Select(part => part.Layer).OfType<SceneLayer>()], setup.Camera, setup.Lights) { Meshes = [.. parts.SelectMany(part => part.Meshes)] },
            output.Width,
            output.Height,
            Matrix3x2.Identity,
            LayerNode.NoCrop,
            1.0f,
            BlendMode.Normal,
            [],
            options.Scale);

        MotionBlur? blur = SceneBlur(sequence, time, parts);
        if (blur is null || options.MaxBlurSamples <= 1)
        {
            return still;
        }

        (SceneCamera Camera, ImmutableArray<SceneLight> Lights) SetupAt(Flicks at) => at == time ? setup : SceneAt(project, sequence, at, options, frames);
        IReadOnlyList<Flicks> moments = blur.Moments(time, frameRate, options.MaxBlurSamples);
        if (moments.Count < 2)
        {
            return still;
        }

        // How far anything travels on screen across the shutter, in output pixels: no more
        // moments than that needs, and none at all when nothing moves.
        float Travel(Flicks from, Flicks to)
        {
            SceneCamera a = SetupAt(from).Camera;
            SceneCamera b = SetupAt(to).Camera;
            float most = 0.0f;
            foreach (ScenePart part in parts)
            {
                foreach ((Vector3 p, Vector3 q) in part.Corners(from).Zip(part.Corners(to)))
                {
                    if (a.Project(p) is { } seen && b.Project(q) is { } moved)
                    {
                        most = MathF.Max(most, Vector2.Distance(seen, moved) * options.Scale);
                    }
                }
            }

            return most;
        }

        float travel = Travel(moments[0], moments[moments.Count / 2]) + Travel(moments[moments.Count / 2], moments[^1]);
        if (travel < 0.5f)
        {
            return still;
        }

        moments = blur.Moments(time, frameRate, Math.Min((int)MathF.Ceiling(travel) + 1, options.MaxBlurSamples));
        var samples = ImmutableArray.CreateBuilder<LayerNode>(moments.Count);
        foreach (Flicks at in moments)
        {
            (SceneCamera camera, ImmutableArray<SceneLight> lights) = SetupAt(at);
            samples.Add(still with
            {
                Source = new SceneLayerSource([.. parts.Select(part => part.At(at)).OfType<SceneLayer>()], camera, lights)
                {
                    Meshes = [.. parts.SelectMany(part => part.MeshesAt?.Invoke(at) ?? part.Meshes)],
                },
            });
        }

        return new LayerNode(new MotionBlurLayerSource(samples.MoveToImmutable()), output.Width, output.Height, Matrix3x2.Identity, LayerNode.NoCrop, 1.0f, BlendMode.Normal, [], options.Scale);
    }

    /// <summary>The motion blur a scene takes: its camera's (from the clip, its track or the sequence), or the first of its layers' that is on.</summary>
    private static MotionBlur? SceneBlur(Sequence sequence, Flicks time, IReadOnlyList<ScenePart> parts)
    {
        foreach (Track track in sequence.Tracks.Reverse())
        {
            if (track.Kind == TrackKind.Video && !track.Muted && TimelineQueries.ClipAt(track, time) is { Enabled: true, GeneratorId: SceneObjects.Camera } camera)
            {
                if (MotionBlur.For(camera, track, sequence) is { Enabled: true } own)
                {
                    return own;
                }

                break;
            }
        }

        return parts.Select(part => MotionBlur.For(part.Clip, part.Track, sequence)).FirstOrDefault(setting => setting is { Enabled: true });
    }

    /// <summary>How a 3D clip takes light at a moment.</summary>
    private static SceneMaterial MaterialOf(Clip clip, Flicks local)
    {
        Layer3D space = clip.Layer3D ?? Layer3D.Default;
        return new SceneMaterial(
            space.AcceptsLights,
            space.CastsShadows,
            space.AcceptsShadows,
            Math.Clamp(Float(space.Ambient, Intrinsic.Ambient, local), 0.0f, 1.0f),
            Math.Clamp(Float(space.Diffuse, Intrinsic.Diffuse, local), 0.0f, 1.0f),
            Math.Clamp(Float(space.Specular, Intrinsic.Specular, local), 0.0f, 1.0f),
            Math.Clamp(Float(space.Roughness, Intrinsic.Roughness, local), 0.0f, 1.0f));
    }

    /// <summary>
    /// A 3D clip as a scene layer: its picture drawn alone into a canvas at the resolution it is
    /// seen at, with its effects and masks, and the matrix that sets the canvas in the world.
    /// Null when it has no picture or no size. Inside a transition its picture is asked for on
    /// the side's own decoder <paramref name="lane"/>, at the source time <paramref name="shown"/>.
    /// </summary>
    private static ScenePart? SceneLayerOf(
        Project project,
        Sequence sequence,
        Track track,
        Clip clip,
        Flicks time,
        Vector2 frameSize,
        (int Width, int Height) output,
        IFrameProvider frames,
        RenderOptions options,
        int depth,
        Rational frameRate,
        SceneCamera camera,
        AcesOutput? aces,
        int? lane = null,
        Flicks? shown = null)
    {
        Flicks local = time - clip.Start;
        Flicks picture = shown ?? time;
        if (Source(project, clip, picture, lane ?? track.Order, frameSize, output, frames, options, depth, frameRate) is not { } source)
        {
            return null;
        }

        Transform transform = clip.Transform ?? Transform.Identity;
        Vector2 scale = Float2(transform.Scale, Intrinsic.Scale, local);
        Vector2 fit = FitScale(source.Size, frameSize, source.Policy);
        Vector2 fitted = source.Size * fit;
        if (scale.X == 0.0f || scale.Y == 0.0f || fitted.X <= 0.0f || fitted.Y <= 0.0f)
        {
            return null;
        }

        Matrix4x4 placement = LayerPlacement(clip, local, fit);
        ImmutableArray<EffectNode> effects = PersonMattes(project, clip, picture, Effects(clip, track, local, time, sequence, options), frames);

        // Canvas texels per sequence pixel of the unscaled picture: what one of them spans on
        // screen, so the canvas is drawn at about the size it is seen and neither blurs nor
        // shimmers. Stepped by quarter octaves from the working scale, so a layer at rest is
        // drawn at exactly the working scale, texel for pixel, and a slow dolly does not change
        // the canvas every frame.
        float distance = camera.Depth(Vector3.Transform(Vector3.Zero, placement));
        float seen = distance > SceneCamera.Near ? camera.Zoom / distance * options.Scale : options.Scale;
        float wanted = seen * MathF.Max(MathF.Abs(scale.X), MathF.Abs(scale.Y));
        float steps = Math.Clamp(MathF.Round(MathF.Log2(wanted / options.Scale) * 4.0f) / 4.0f, -4.0f, 2.0f);
        float texels = options.Scale * MathF.Pow(2.0f, steps);

        // Effects can draw past the picture (a glow, a shadow), so they get room round it.
        float room = effects.IsDefaultOrEmpty ? 0.0f : MathF.Max(fitted.X, fitted.Y) * 0.25f;
        float largest = MathF.Max(fitted.X, fitted.Y) + (2.0f * room);
        if (largest * texels > MostCanvas - (2 * CanvasMargin))
        {
            texels = (MostCanvas - (2 * CanvasMargin)) / largest;
        }

        int margin = CanvasMargin + (int)MathF.Ceiling(room * texels);
        int width = (int)MathF.Ceiling(fitted.X * texels) + (2 * margin);
        int height = (int)MathF.Ceiling(fitted.Y * texels) + (2 * margin);
        var canvasSize = new Vector2(width, height);

        Matrix3x2 steady = Steady(project, clip, picture, source.Size, frames, options);
        Matrix3x2 intoCanvas = steady
            * Matrix3x2.CreateTranslation(-source.Size / 2.0f)
            * Matrix3x2.CreateScale(fit * texels)
            * Matrix3x2.CreateTranslation(canvasSize / 2.0f);

        var drawn = new LayerNode(
            source.Source,
            (int)source.Size.X,
            (int)source.Size.Y,
            intoCanvas,
            CropRect(clip.Crop, local),
            1.0f,
            BlendMode.Normal,
            Mattes(clip.Masks, local),
            texels)
        {
            // A comp graph on a 3D layer works in its canvas: that is its frame.
            Effects = Composited(project, clip, time, effects, frames, canvasSize / texels, (width, height), texels, options, depth, frameRate),
        };

        var canvas = new RenderGraph(width, height, [drawn])
        {
            Bicubic = options.Bicubic,
            ProjectFolder = options.ProjectFolder,
            CacheLayers = options.CacheLayers,
            Aces = aces,
        };

        Matrix4x4 intoPicture = Matrix4x4.CreateTranslation(new Vector3(-canvasSize / 2.0f, 0.0f))
            * Matrix4x4.CreateScale(1.0f / texels, 1.0f / texels, 1.0f);
        Matrix4x4 world = intoPicture * placement;

        return new ScenePart(track, clip, new SceneLayer(canvas, world, Float(clip.Opacity, Intrinsic.Opacity, local), MaterialOf(clip, local)), intoPicture, fit);
    }

    /// <summary>Text, a shape or a model as a scene part: its meshes now, and how to make them at another moment.</summary>
    private static ScenePart MeshPartOf(Track track, Clip clip, Flicks time, RenderOptions options)
    {
        ImmutableArray<SceneMesh> At(Flicks moment) => MeshesOf(clip, moment, options);
        return new ScenePart(track, clip, null, Matrix4x4.Identity, Vector2.One) { Meshes = At(time), MeshesAt = At };
    }

    /// <summary>The meshes a text, shape or model clip draws at a moment, each placed in the world.</summary>
    private static ImmutableArray<SceneMesh> MeshesOf(Clip clip, Flicks time, RenderOptions options)
    {
        string type = clip.GeneratorId!;
        if (OwnParameters(clip, type, time, options) is not { } p)
        {
            return [];
        }

        Flicks local = time - clip.Start;
        return MeshesFrom(
            type,
            p,
            local,
            LayerPlacement(clip, local, Vector2.One),
            Float(clip.Opacity, Intrinsic.Opacity, local),
            clip.Layer3D?.CastsShadows ?? true,
            clip.Layer3D?.AcceptsShadows ?? true,
            clip.Layer3D?.AcceptsLights ?? true,
            options);
    }

    /// <summary>The meshes of text, a shape or a model from its parameters at a moment, each placed in the world.</summary>
    private static ImmutableArray<SceneMesh> MeshesFrom(string type, ParameterSet p, Flicks local, Matrix4x4 placement, float opacity, bool casts, bool accepts, bool lights, RenderOptions options)
    {
        SceneMesh Placed(MeshData mesh, ImmutableArray<PbrMaterial> materials, Matrix4x4 own) => new(mesh, materials, own * placement, opacity, casts, accepts, lights);
        PbrMaterial Painted(Vector4 color) => new(color, Math.Clamp(p.Float("metallic"), 0.0f, 1.0f), Math.Clamp(p.Float("roughness"), 0.0f, 1.0f));

        switch (type)
        {
            case SceneObjects.Text:
            {
                var shape = new TextShape(
                    p.Text("text"),
                    p.Text("font"),
                    p.Enum("weight") switch
                    {
                        "thin" => Vortice.DirectWrite.FontWeight.Thin,
                        "light" => Vortice.DirectWrite.FontWeight.Light,
                        "regular" => Vortice.DirectWrite.FontWeight.Normal,
                        "medium" => Vortice.DirectWrite.FontWeight.Medium,
                        "semibold" => Vortice.DirectWrite.FontWeight.SemiBold,
                        "black" => Vortice.DirectWrite.FontWeight.Black,
                        _ => Vortice.DirectWrite.FontWeight.Bold,
                    },
                    p.Bool("italic"),
                    p.Float("size"),
                    p.Enum("align"),
                    p.Float("depth"),
                    p.Float("bevel"),
                    p.Int("bevel-segments"),
                    options.ProjectFolder);
                MeshData mesh = MeshLibrary.Text(shape);
                return mesh.Triangles == 0 ? [] : [Placed(mesh, [Painted(p.Color("color")), Painted(p.Color("side-color"))], Matrix4x4.Identity)];
            }

            case SceneObjects.Shape:
            {
                ShapeKind kind = p.Enum("kind") switch
                {
                    "sphere" => ShapeKind.Sphere,
                    "cylinder" => ShapeKind.Cylinder,
                    "cone" => ShapeKind.Cone,
                    "torus" => ShapeKind.Torus,
                    "plane" => ShapeKind.Plane,
                    _ => ShapeKind.Cube,
                };
                MeshData mesh = MeshLibrary.Shape(kind, new Vector3(p.Float2("size"), p.Float("depth")), p.Int("segments"), p.Float("thickness"));
                return [Placed(mesh, [Painted(p.Color("color")) with { DoubleSided = kind == ShapeKind.Plane }], Matrix4x4.Identity)];
            }

            case SceneObjects.Model:
            {
                if (ModelAt(p, local, options) is not { } at)
                {
                    return [];
                }

                return [.. at.Model.Pose(at.Animation, at.Seconds).Select(drawn => Placed(drawn.Drawn, at.Model.MeshMaterials[drawn.Mesh], drawn.World * at.IntoScene))];
            }

            default:
                return [];
        }
    }

    /// <summary>
    /// A model clip's model at a moment: the file read, the matrix from its own space into the
    /// scene's (centred and sized), and which animation is at what time (-1 for at rest); null
    /// when it names no file that can be read.
    /// </summary>
    private static (GltfModel Model, Matrix4x4 IntoScene, int Animation, float Seconds)? ModelAt(ParameterSet p, Flicks local, RenderOptions options)
    {
        string file = p.Text("file");
        if (file.Length == 0)
        {
            return null;
        }

        string path = System.IO.Path.IsPathRooted(file) || options.ProjectFolder.Length == 0 ? file : System.IO.Path.Combine(options.ProjectFolder, file);
        if (MeshLibrary.Model(path, out _) is not { } model)
        {
            return null;
        }

        // glTF is y up and z towards the viewer, in metres; turned half round X into this
        // space, centred, and sized so its largest side is the size asked for.
        (Vector3 low, Vector3 high) = model.Bounds();
        float largest = MathF.Max(high.X - low.X, MathF.Max(high.Y - low.Y, high.Z - low.Z));
        float scale = p.Float("size") / MathF.Max(largest, 1e-6f);
        Matrix4x4 intoScene = Matrix4x4.CreateTranslation(-(low + high) / 2.0f) * Matrix4x4.CreateScale(scale, -scale, -scale);

        int animation = AnimationIndex(model, p.Text("animation"));
        float seconds = 0.0f;
        if (animation >= 0 && p.Bool("animate"))
        {
            seconds = (float)local.ToSeconds() * p.Float("speed");
            float length = model.Animations[animation].Duration;
            if (p.Bool("loop") && length > 0.0f)
            {
                seconds %= length;
                if (seconds < 0.0f)
                {
                    seconds += length;
                }
            }
        }

        return (model, intoScene, p.Bool("animate") ? animation : -1, seconds);
    }

    /// <summary>
    /// The camera of a model clip that asks to be looked through one of its own: the node's place
    /// carried into the scene, looking down its -Z with +Y up as glTF has it, and its vertical
    /// field of view across the frame's height; null when the clip names none it has.
    /// </summary>
    private static SceneCamera? ModelCamera(Clip clip, Flicks time, Vector2 frameSize, RenderOptions options)
    {
        Flicks local = time - clip.Start;
        if (OwnParameters(clip, SceneObjects.Model, time, options) is not { } p || p.Text("camera").Trim() is not { Length: > 0 } chosen
            || ModelAt(p, local, options) is not { } at)
        {
            return null;
        }

        GltfModel model = at.Model;
        int wanted = int.TryParse(chosen, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int number)
            ? number
            : Enumerable.Range(0, model.Cameras.Length).FirstOrDefault(index => string.Equals(model.Cameras[index].Name, chosen, StringComparison.OrdinalIgnoreCase), -1);
        (int Camera, Matrix4x4 World)? found = model.CamerasAt(at.Animation, at.Seconds)
            .Where(candidate => candidate.Camera == wanted)
            .Select(candidate => ((int, Matrix4x4)?)candidate)
            .FirstOrDefault();
        if (found is not { } seen)
        {
            return null;
        }

        Matrix4x4 world = seen.World * at.IntoScene * LayerPlacement(clip, local, Vector2.One);
        Vector3 forward = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, world));
        Vector3 right = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, world));
        Vector3 down = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitY, world));
        float zoom = frameSize.Y / 2.0f / MathF.Tan(Math.Clamp(model.Cameras[seen.Camera].YFov, 0.01f, 3.1f) / 2.0f);
        return new SceneCamera(Vector3.Transform(Vector3.Zero, world), right, down, forward, zoom, frameSize);
    }

    /// <summary>
    /// The lights of a model clip that lights the scene with its own, as a glTF viewer does: a point
    /// or spot light's intensity at a metre of the model's, falling as the inverse square beyond,
    /// and a directional light's as its strength.
    /// </summary>
    private static IEnumerable<SceneLight> ModelLights(Clip clip, Flicks time, RenderOptions options)
    {
        Flicks local = time - clip.Start;
        if (OwnParameters(clip, SceneObjects.Model, time, options) is not { } p || !p.Bool("lights") || ModelAt(p, local, options) is not { } at)
        {
            yield break;
        }

        Matrix4x4 placement = at.IntoScene * LayerPlacement(clip, local, Vector2.One);
        float metre = Vector3.TransformNormal(Vector3.UnitX, placement).Length();
        foreach ((int index, Matrix4x4 node) in at.Model.LightsAt(at.Animation, at.Seconds))
        {
            GltfLight light = at.Model.Lights[index];
            Matrix4x4 world = node * placement;
            Vector3 position = Vector3.Transform(Vector3.Zero, world);
            Vector3 direction = Vector3.TransformNormal(-Vector3.UnitZ, world);
            direction = direction.LengthSquared() > 1e-12f ? Vector3.Normalize(direction) : Vector3.UnitZ;
            Vector3 color = light.Color * MathF.Max(light.Intensity, 0.0f);
            yield return light.Kind switch
            {
                GltfLightKind.Directional => new SceneLight(SceneLightKind.Directional, color, position, direction),
                GltfLightKind.Spot => new SceneLight(
                    SceneLightKind.Spot,
                    color,
                    position,
                    direction,
                    light.OuterCone * 2.0f * 180.0f / MathF.PI,
                    light.OuterCone > 0.0f ? Math.Clamp((light.OuterCone - light.InnerCone) / light.OuterCone, 0.0f, 1.0f) : 0.5f,
                    SceneFalloff.InverseSquare,
                    metre),
                _ => new SceneLight(SceneLightKind.Point, color, position, direction, Falloff: SceneFalloff.InverseSquare, Radius: metre),
            };
        }
    }

    /// <summary>Which of a model's animations a name or a number picks: the first when it says nothing, none when it has none.</summary>
    private static int AnimationIndex(GltfModel model, string chosen)
    {
        if (model.Animations.IsEmpty)
        {
            return -1;
        }

        if (chosen.Length == 0)
        {
            return 0;
        }

        if (int.TryParse(chosen, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int number))
        {
            return number >= 0 && number < model.Animations.Length ? number : 0;
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

    private static SceneCamera CameraOf(Clip clip, Flicks time, Vector2 frameSize, RenderOptions options) =>
        OwnParameters(clip, SceneObjects.Camera, time, options) is { } p ? CameraFrom(p, frameSize) : DefaultCamera(frameSize);

    /// <summary>A camera from its parameters.</summary>
    private static SceneCamera CameraFrom(ParameterSet p, Vector2 frameSize)
    {
        float dolly = p.Float("dolly");
        var target = new Vector3(p.Float2("target"), p.Float("target-z"));
        (Vector3 position, Vector3 right, Vector3 down, Vector3 forward, float zoom) = SceneMath.Camera(
            frameSize,
            p.Float2("position"),
            dolly,
            target,
            p.Float("orbit"),
            p.Float("tilt"),
            p.Float("roll"),
            p.Float("angle"),
            CameraGenerator.RestAngle);

        float focus = p.Float("focus");
        if (focus <= 0.0f)
        {
            // Focused on the point of interest, which a dolly carries along.
            focus = Vector3.Distance(position, target + new Vector3(0.0f, 0.0f, dolly));
        }

        return new SceneCamera(position, right, down, forward, zoom, frameSize, p.Bool("depth-of-field"), focus, MathF.Max(p.Float("aperture"), 0.0f));
    }

    private static SceneLight? LightOf(Project project, Clip clip, Flicks time, RenderOptions options, IFrameProvider? frames) =>
        OwnParameters(clip, SceneObjects.Light, time, options) is { } p ? LightFrom(project, clip.Id, p, frames) : null;

    /// <summary>A light from its parameters; <paramref name="id"/> keeps an environment light's picture apart from any other.</summary>
    private static SceneLight LightFrom(Project project, string id, ParameterSet p, IFrameProvider? frames)
    {
        SceneLightKind kind = p.Enum("kind") switch
        {
            "ambient" => SceneLightKind.Ambient,
            "environment" => SceneLightKind.Environment,
            "spot" => SceneLightKind.Spot,
            "directional" => SceneLightKind.Directional,
            _ => SceneLightKind.Point,
        };

        var position = new Vector3(p.Float2("position"), p.Float("position-z"));
        var target = new Vector3(p.Float2("target"), p.Float("target-z"));
        Vector3 direction = target - position;
        direction = direction.LengthSquared() > 1e-8f ? Vector3.Normalize(direction) : Vector3.UnitZ;

        Vector4 color = p.Color("color");
        float intensity = MathF.Max(p.Float("intensity"), 0.0f);

        // An environment light's picture is a media item's frame, fetched as any clip's is.
        LayerSource? image = null;
        Vector2 imageSize = Vector2.Zero;
        if (kind == SceneLightKind.Environment && frames is not null && p.Text("image") is { Length: > 0 } mediaId
            && project.MediaItem(mediaId) is { } media)
        {
            var still = new Clip(id + ":environment", new TimeRange(Flicks.Zero, Flicks.Max(media.Duration, new Flicks(1))), Flicks.Zero, MediaId: media.Id);
            if (frames.Frame(project, still, Flicks.Zero, EnvironmentLane) is { } frame)
            {
                image = new FrameLayerSource(frame.Frame, frame.Color, frame.Identity);
                imageSize = frame.Width > 0 && frame.Height > 0 ? new Vector2(frame.Width, frame.Height) : new Vector2(frame.Frame.Width, frame.Frame.Height);
            }
        }

        return new SceneLight(
            kind,
            new Vector3(color.X, color.Y, color.Z) * intensity,
            position,
            direction,
            p.Float("cone-angle"),
            p.Float("cone-feather") / 100.0f,
            p.Enum("falloff") switch
            {
                "smooth" => SceneFalloff.Smooth,
                "inverse-square" => SceneFalloff.InverseSquare,
                _ => SceneFalloff.None,
            },
            MathF.Max(p.Float("radius"), 0.0f),
            MathF.Max(p.Float("falloff-distance"), 1.0f),
            p.Bool("casts-shadows"),
            p.Float("shadow-darkness"),
            MathF.Max(p.Float("shadow-softness"), 0.0f))
        {
            Image = image,
            ImageSize = imageSize,
        };
    }

    /// <summary>The decoder lane an environment light's picture is fetched on, apart from every track's.</summary>
    private const int EnvironmentLane = 1 << 20;

    /// <summary>A camera's or a light's own parameters at a time, or null when the registry does not have its type.</summary>
    private static ParameterSet? OwnParameters(Clip clip, string type, Flicks time, RenderOptions options)
    {
        if (options.Effects.Find(type) is not { } descriptor)
        {
            return null;
        }

        DriverScope.Origin = clip.Start;
        Effect? own = clip.Effects.FirstOrDefault(effect => string.Equals(effect.TypeId, type, StringComparison.Ordinal));
        return own is null ? ParameterSet.Defaults(descriptor) : ParameterSet.Evaluate(descriptor, own, time - clip.Start);
    }
}
