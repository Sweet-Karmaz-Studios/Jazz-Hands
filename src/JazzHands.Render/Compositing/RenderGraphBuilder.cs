using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Core.Animation;
using JazzHands.Core.Model;
using JazzHands.Core.Queries;
using JazzHands.Core.Time;
using JazzHands.Render.Color;
using JazzHands.Render.Frames;

namespace JazzHands.Render.Compositing;

/// <summary>A decoded picture for one clip at one moment, as the engine hands it over.</summary>
/// <param name="Frame">The frame, owned by the engine's cache; valid for the render.</param>
/// <param name="Color">How to turn its samples into light.</param>
/// <param name="Identity">What it is (media, stream, source time), for the layer cache.</param>
public sealed record SourceFrame(FrameTexture Frame, YuvColorSpace Color, string Identity)
{
    /// <summary>
    /// The width of the picture this frame stands for, when that is not the texture's: a proxy at
    /// half size is placed as the source it replaces. Zero for the texture's own.
    /// </summary>
    public int Width { get; init; }

    /// <summary>The height of the picture this frame stands for; zero for the texture's own.</summary>
    public int Height { get; init; }
}

/// <summary>
/// Where the builder gets decoded pictures. The engine implements it over its frame server; the
/// render layer may not see decoders.
/// </summary>
public interface IFrameProvider
{
    /// <summary>The picture a media clip shows at a timeline time, or null when it has none there.</summary>
    /// <param name="project">The project, for the clip's media.</param>
    /// <param name="clip">The clip.</param>
    /// <param name="timelineTime">When, on the timeline the clip is on.</param>
    /// <param name="lane">
    /// Which stacking position asks, so two layers playing the same file at different times use
    /// different decoders rather than dragging one back and forth.
    /// </param>
    SourceFrame? Frame(Project project, Clip clip, Flicks timelineTime, int lane);
}

/// <summary>How a frame is built.</summary>
public sealed record RenderOptions
{
    /// <summary>Output pixels per sequence pixel: 1 for Full, 0.5 for Half, 0.25 for Quarter.</summary>
    public float Scale { get; init; } = 1.0f;

    /// <summary>Bicubic rather than bilinear sampling for placed layers.</summary>
    public bool Bicubic { get; init; } = true;

    /// <summary>Keep placed layers between frames: on while scrubbing, off while playing.</summary>
    public bool CacheLayers { get; init; }

    /// <summary>
    /// Turns an effect instance into something the compositor can run, or null for one it does
    /// not know. Phase 15's registry plugs in here; without it adjustment layers pass through.
    /// </summary>
    public Func<Effect, ILayerEffect?>? Effects { get; init; }

    /// <summary>How deep nested sequences may go before the builder stops, whatever the validator allowed.</summary>
    public int MaxNesting { get; init; } = 16;

    /// <summary>What the preview gets at a given quality divisor: 1, 2 or 4.</summary>
    public static RenderOptions ForDivisor(int divisor) => new() { Scale = 1.0f / Math.Max(1, divisor) };
}

/// <summary>
/// Turns a sequence at a moment into a <see cref="RenderGraph"/>: which clips are on screen, in
/// what order, and every animated value evaluated.
/// </summary>
/// <remarks>
/// Video and adjustment tracks are stacked by their order, bottom first; a muted video track is a
/// hidden one. A clip contributes when it is enabled and its range covers the time. Media clips
/// ask the <see cref="IFrameProvider"/> for their picture; <c>gen.solid</c> generators are a flat
/// colour; compound clips build their own sequence's graph at the time inside it, recursively.
///
/// Placement is one matrix per layer, from the picture's own pixels to output pixels:
/// the media's fit into the frame, then the clip's anchor, scale, rotation (degrees clockwise) and
/// position (pixels from the frame centre), then the preview quality. Keyframe times are relative
/// to the clip's start, so moving a clip moves its animation with it.
/// </remarks>
public static class RenderGraphBuilder
{
    /// <summary>The generator type for a flat colour.</summary>
    public const string SolidGenerator = "gen.solid";

    /// <summary>Builds the graph for a sequence at a time.</summary>
    public static RenderGraph Build(Project project, Sequence sequence, Flicks time, IFrameProvider frames, RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(options);

        return Build(project, sequence, time, frames, options, depth: 0);
    }

    /// <summary>The output size for a sequence at a quality.</summary>
    public static (int Width, int Height) OutputSize(ProjectSettings settings, float scale)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return (Math.Max(1, (int)MathF.Ceiling(settings.Width * scale)), Math.Max(1, (int)MathF.Ceiling(settings.Height * scale)));
    }

    /// <summary>
    /// The matrix from a picture's pixels to output pixels.
    /// </summary>
    /// <param name="pictureSize">The picture in its own pixels.</param>
    /// <param name="frameSize">The sequence frame.</param>
    /// <param name="policy">How the picture is fitted before the clip's transform.</param>
    /// <param name="position">Offset of the picture's centre from the frame centre, in sequence pixels.</param>
    /// <param name="scale">Scale per axis.</param>
    /// <param name="rotationDegrees">Clockwise.</param>
    /// <param name="anchor">The pivot, in picture pixels from the picture's centre.</param>
    /// <param name="outputScale">Output pixels per sequence pixel.</param>
    public static Matrix3x2 Placement(
        Vector2 pictureSize,
        Vector2 frameSize,
        ConformPolicy policy,
        Vector2 position,
        Vector2 scale,
        float rotationDegrees,
        Vector2 anchor,
        float outputScale)
    {
        Vector2 fit = FitScale(pictureSize, frameSize, policy);
        Vector2 centre = pictureSize / 2.0f;

        // Move the pivot to the origin, fit and scale about it, turn, then put the pivot back
        // where it was and the picture where the clip says. With no scale, rotation or position
        // the anchor makes no difference, which is what a pivot has to do.
        return Matrix3x2.CreateTranslation(-(centre + anchor))
            * Matrix3x2.CreateScale(fit * scale)
            * Matrix3x2.CreateRotation(rotationDegrees * MathF.PI / 180.0f)
            * Matrix3x2.CreateTranslation((anchor * fit) + (frameSize / 2.0f) + position)
            * Matrix3x2.CreateScale(outputScale);
    }

    /// <summary>How a picture is scaled to sit in the frame before any transform.</summary>
    public static Vector2 FitScale(Vector2 pictureSize, Vector2 frameSize, ConformPolicy policy)
    {
        if (pictureSize.X <= 0 || pictureSize.Y <= 0)
        {
            return Vector2.One;
        }

        Vector2 ratio = frameSize / pictureSize;

        return policy switch
        {
            ConformPolicy.Stretch => ratio,
            ConformPolicy.Native => Vector2.One,
            ConformPolicy.Fill => new Vector2(MathF.Max(ratio.X, ratio.Y)),
            _ => new Vector2(MathF.Min(ratio.X, ratio.Y)),
        };
    }

    private static RenderGraph Build(Project project, Sequence sequence, Flicks time, IFrameProvider frames, RenderOptions options, int depth)
    {
        ProjectSettings settings = project.SettingsFor(sequence);
        (int width, int height) = OutputSize(settings, options.Scale);
        var frameSize = new Vector2(settings.Width, settings.Height);
        var layers = ImmutableArray.CreateBuilder<LayerNode>();

        // Tracks are kept in stacking order by every edit and by loading, so this is bottom first.
        foreach (Track track in sequence.Tracks)
        {
            if (track.Kind is not (TrackKind.Video or TrackKind.Adjustment) || track.Muted)
            {
                continue;
            }

            if (TimelineQueries.ClipAt(track, time) is not { Enabled: true } clip)
            {
                continue;
            }

            Flicks local = time - clip.Start;

            if (track.Kind == TrackKind.Adjustment)
            {
                layers.Add(Adjustment(clip, local, frameSize, options));
                continue;
            }

            if (Source(project, clip, time, track.Order, frameSize, frames, options, depth) is not { } source)
            {
                continue;
            }

            layers.Add(Layer(clip, local, source, frameSize, options));
        }

        return new RenderGraph(width, height, layers.ToImmutable())
        {
            Bicubic = options.Bicubic,
            CacheLayers = options.CacheLayers,
        };
    }

    /// <summary>What a picture clip shows, and how big it is in its own pixels.</summary>
    private static (LayerSource Source, Vector2 Size, ConformPolicy Policy)? Source(
        Project project,
        Clip clip,
        Flicks time,
        int lane,
        Vector2 frameSize,
        IFrameProvider frames,
        RenderOptions options,
        int depth)
    {
        if (clip.MediaId is { } mediaId)
        {
            if (frames.Frame(project, clip, time, lane) is not { } frame)
            {
                return null;
            }

            ConformPolicy policy = project.MediaItem(mediaId)?.Conform ?? ConformPolicy.Fit;
            Vector2 size = frame.Width > 0 && frame.Height > 0
                ? new Vector2(frame.Width, frame.Height)
                : new Vector2(frame.Frame.Width, frame.Frame.Height);

            return (new FrameLayerSource(frame.Frame, frame.Color, frame.Identity), size, policy);
        }

        if (clip.SequenceId is { } sequenceId)
        {
            if (depth >= options.MaxNesting || project.Sequence(sequenceId) is not { } nested)
            {
                return null;
            }

            // The nested sequence plays its own time: where the clip's source in and speed put it.
            RenderGraph inner = Build(project, nested, clip.SourceTimeAt(time), frames, options, depth + 1);
            ProjectSettings innerSettings = project.SettingsFor(nested);
            return (new NestedLayerSource(inner), new Vector2(innerSettings.Width, innerSettings.Height), ConformPolicy.Fit);
        }

        if (string.Equals(clip.GeneratorId, SolidGenerator, StringComparison.Ordinal))
        {
            Vector4 colour = GeneratorColour(clip, time - clip.Start);

            return (new SolidLayerSource(new Vector4(colour.X * colour.W, colour.Y * colour.W, colour.Z * colour.W, colour.W)), frameSize, ConformPolicy.Stretch);
        }

        return null;
    }

    private static LayerNode Layer(
        Clip clip,
        Flicks local,
        (LayerSource Source, Vector2 Size, ConformPolicy Policy) source,
        Vector2 frameSize,
        RenderOptions options)
    {
        Transform transform = clip.Transform ?? Transform.Identity;

        Matrix3x2 placement = Placement(
            source.Size,
            frameSize,
            source.Policy,
            Float2(transform.Position, local, Vector2.Zero),
            Float2(transform.Scale, local, Vector2.One),
            Float(transform.Rotation, local, 0.0f),
            Float2(transform.Anchor, local, Vector2.Zero),
            options.Scale);

        return new LayerNode(
            source.Source,
            (int)source.Size.X,
            (int)source.Size.Y,
            placement,
            CropRect(clip.Crop, local),
            Math.Clamp(Float(clip.Opacity, local, 1.0f), 0.0f, 1.0f),
            clip.BlendMode,
            Mattes(clip, local),
            options.Scale);
    }

    private static LayerNode Adjustment(Clip clip, Flicks local, Vector2 frameSize, RenderOptions options)
    {
        var effects = ImmutableArray.CreateBuilder<ILayerEffect>();
        if (options.Effects is { } resolve)
        {
            foreach (Effect effect in clip.Effects)
            {
                if (effect.Enabled && resolve(effect) is { } runnable)
                {
                    effects.Add(runnable);
                }
            }
        }

        // An adjustment layer's masks are drawn in frame pixels, so its picture is the frame.
        Matrix3x2 frame = Matrix3x2.CreateScale(options.Scale);

        return new LayerNode(
            new SolidLayerSource(Vector4.Zero),
            (int)frameSize.X,
            (int)frameSize.Y,
            frame,
            LayerNode.NoCrop,
            Math.Clamp(Float(clip.Opacity, local, 1.0f), 0.0f, 1.0f),
            clip.BlendMode,
            Mattes(clip, local),
            options.Scale)
        {
            Effects = effects.ToImmutable(),
            IsAdjustment = true,
        };
    }

    private static ImmutableArray<MatteShape> Mattes(Clip clip, Flicks local)
    {
        if (clip.Masks.IsEmpty)
        {
            return [];
        }

        var shapes = ImmutableArray.CreateBuilder<MatteShape>();
        foreach (Mask mask in clip.Masks)
        {
            if (!mask.Enabled)
            {
                continue;
            }

            shapes.Add(new MatteShape(
                mask.Shape,
                Float4(mask.Bounds, local, Vector4.Zero),
                mask.PathData is null ? string.Empty : Text(mask.PathData, local),
                MathF.Max(0.0f, Float(mask.Feather, local, 0.0f)),
                Math.Clamp(Float(mask.Opacity, local, 1.0f), 0.0f, 1.0f),
                mask.Mode,
                mask.Invert));
        }

        return shapes.ToImmutable();
    }

    /// <summary>A crop in percent per side as the texture coordinates of what is kept.</summary>
    private static Vector4 CropRect(Crop? crop, Flicks local)
    {
        if (crop is null)
        {
            return LayerNode.NoCrop;
        }

        float left = Math.Clamp(Float(crop.Left, local, 0.0f), 0.0f, 100.0f) / 100.0f;
        float top = Math.Clamp(Float(crop.Top, local, 0.0f), 0.0f, 100.0f) / 100.0f;
        float right = Math.Clamp(Float(crop.Right, local, 0.0f), 0.0f, 100.0f) / 100.0f;
        float bottom = Math.Clamp(Float(crop.Bottom, local, 0.0f), 0.0f, 100.0f) / 100.0f;

        return new Vector4(left, top, MathF.Max(left, 1.0f - right), MathF.Max(top, 1.0f - bottom));
    }

    private static Vector4 GeneratorColour(Clip clip, Flicks local)
    {
        foreach (Effect effect in clip.Effects)
        {
            if (string.Equals(effect.TypeId, clip.GeneratorId, StringComparison.Ordinal)
                && effect.Parameter("color") is { } colour
                && AnimationEvaluator.Evaluate(colour, local) is ParamValue.Color value)
            {
                return value.Value;
            }
        }

        return new Vector4(0.5f, 0.5f, 0.5f, 1.0f);
    }

    private static float Float(AnimatedValue? value, Flicks time, float fallback) =>
        value is not null && AnimationEvaluator.Evaluate(value, time) is ParamValue.Float result ? result.Value : fallback;

    private static Vector2 Float2(AnimatedValue? value, Flicks time, Vector2 fallback) =>
        value is null
            ? fallback
            : AnimationEvaluator.Evaluate(value, time) switch
            {
                ParamValue.Float2 pair => pair.Value,
                ParamValue.Float single => new Vector2(single.Value),
                _ => fallback,
            };

    private static Vector4 Float4(AnimatedValue? value, Flicks time, Vector4 fallback) =>
        value is not null && AnimationEvaluator.Evaluate(value, time) is ParamValue.Float4 result ? result.Value : fallback;

    private static string Text(AnimatedValue value, Flicks time) =>
        AnimationEvaluator.Evaluate(value, time) switch
        {
            ParamValue.Path path => path.Value,
            ParamValue.Text text => text.Value,
            _ => string.Empty,
        };
}
