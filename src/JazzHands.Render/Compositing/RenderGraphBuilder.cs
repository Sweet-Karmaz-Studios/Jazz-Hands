using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Core.Drivers;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Queries;
using JazzHands.Core.Stabilization;
using JazzHands.Core.Subtitles;
using JazzHands.Core.Time;
using JazzHands.Core.Titles;
using JazzHands.Render.Color;
using JazzHands.Render.Effects;
using JazzHands.Render.Effects.Looks;
using JazzHands.Render.Effects.Stabilize;
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

    /// <summary>
    /// When a media clip's file is missing or cannot be read, what its offline slate says under
    /// "Media offline"; null when it is fine, and then a missing picture is only a gap.
    /// </summary>
    string? Offline(Project project, Clip clip) => null;

    /// <summary>
    /// How the camera moved through a media clip's file, when its motion has been analysed for
    /// stabilization; null when it has not.
    /// </summary>
    CameraMotion? Motion(Project project, Clip clip) => null;

    /// <summary>
    /// A track's sound at a moment, from 0 to its loudest, for a driver's <c>audio()</c>: the band,
    /// followed with an attack and a release in seconds. Zero when nothing here can hear it.
    /// </summary>
    double AudioLevel(Project project, Sequence sequence, string trackId, AudioBand band, double seconds, double attack, double release) => 0;
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

    /// <summary>The folder the project file is in, which relative paths in effect parameters (a LUT) are from. Empty for none.</summary>
    public string ProjectFolder { get; init; } = string.Empty;

    /// <summary>
    /// The effect types the builder knows. An effect whose type is not here, or is not a picture
    /// effect, is left out of the frame rather than failing it.
    /// </summary>
    public EffectRegistry Effects { get; init; } = VideoEffects.Registry;

    /// <summary>How deep nested sequences may go before the builder stops, whatever the validator allowed.</summary>
    public int MaxNesting { get; init; } = 16;

    /// <summary>
    /// Draw subtitle tracks that are not muted: on for the preview, and for an export only when
    /// its subtitles are burned in.
    /// </summary>
    public bool Subtitles { get; init; } = true;

    /// <summary>
    /// The most motion blur samples a frame takes, whatever a clip asks for: fewer while the
    /// preview plays or scrubs, all of them for a still or an export.
    /// </summary>
    public int MaxBlurSamples { get; init; } = MotionBlur.MaxSamples;

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

    /// <summary>The transition a blended retime mixes two source frames with.</summary>
    private const string Crossfade = "transition.crossfade";

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
        // Drivers read this sequence's markers, tracks and sound; each clip sets where it starts.
        using DriverScope.Entered drivers = DriverScope.Enter(new ProjectDriverEnvironment(
            project,
            sequence,
            (heard, track, band, seconds, attack, release) => frames.AudioLevel(project, heard, track, band, seconds, attack, release)));

        ProjectSettings settings = project.SettingsFor(sequence);
        (int width, int height) = OutputSize(settings, options.Scale);
        var frameSize = new Vector2(settings.Width, settings.Height);
        var layers = ImmutableArray.CreateBuilder<LayerNode>();

        // A track another uses as its matte is drawn only as that matte.
        HashSet<string> mattes = TrackMatte.Sources(sequence);

        // Tracks are kept in stacking order by every edit and by loading, so this is bottom first.
        foreach (Track track in sequence.Tracks)
        {
            if (track.Kind == TrackKind.Subtitle)
            {
                if (options.Subtitles && !track.Muted)
                {
                    layers.AddRange(Subtitles(track, time, settings, frameSize, options));
                }

                continue;
            }

            if (track.Kind is not (TrackKind.Video or TrackKind.Adjustment) || track.Muted || mattes.Contains(track.Id))
            {
                continue;
            }

            TrackMoment moment = track.Kind == TrackKind.Video
                ? TransitionTiming.At(track, time, settings.FrameRate)
                : new TrackMoment(TimelineQueries.ClipAt(track, time), null, null);

            if (moment.Span is { } span)
            {
                layers.Add(Transition(project, sequence, track, span, time, frameSize, (width, height), frames, options, depth, settings.FrameRate)
                    with { TrackMatte = Matted(project, sequence, track.Matte, time, frames, options, depth) });
                continue;
            }

            if (moment.Clip is not { Enabled: true } clip)
            {
                continue;
            }

            DriverScope.Origin = clip.Start;

            Flicks local = time - clip.Start;

            ImmutableArray<EffectNode> effects = Effects(clip, track, local, time, sequence, options);

            if (track.Kind == TrackKind.Adjustment)
            {
                layers.Add(Adjustment(clip, local, frameSize, options) with { Effects = effects });
                continue;
            }

            if (Source(project, clip, time, track.Order, frameSize, (width, height), frames, options, depth, settings.FrameRate) is not { } source)
            {
                continue;
            }

            LayerNode layer = Layer(clip, local, source, frameSize, options, Steady(project, clip, time, source.Size, frames, options)) with { Effects = effects };
            TrackMatteNode? matte = Matted(project, sequence, TrackMatte.For(clip, track), time, frames, options, depth);
            layers.Add((Echoed(project, track, clip, time, source, layer, frameSize, (width, height), frames, options, depth, settings.FrameRate)
                ?? Blurred(project, sequence, track, clip, time, source, layer, frameSize, (width, height), frames, options, depth, settings.FrameRate)
                ?? layer) with { TrackMatte = matte });
        }

        return new RenderGraph(width, height, layers.ToImmutable())
        {
            Bicubic = options.Bicubic,
            ProjectFolder = options.ProjectFolder,
            CacheLayers = options.CacheLayers,
        };
    }

    /// <summary>
    /// The decoder lane the incoming clip of a transition asks on: its own, so a transition
    /// between two parts of one file keeps two decoders rather than seeking one back and forth.
    /// </summary>
    public static int IncomingLane(int trackOrder) => trackOrder + (1 << 16);

    /// <summary>
    /// A track inside a transition: both clips as layers of their own, at this time, each with its
    /// own place, effects and masks, mixed by the transition at its eased progress.
    /// </summary>
    /// <remarks>
    /// The outgoing clip plays on past its end and the incoming one starts before its start. Where
    /// a file has less source than that, the frame is held at the last there is. Keyframes are
    /// read at the clip's own time, so they hold their end values beyond the clip.
    /// </remarks>
    private static LayerNode Transition(
        Project project,
        Sequence sequence,
        Track track,
        TransitionSpan span,
        Flicks time,
        Vector2 frameSize,
        (int Width, int Height) output,
        IFrameProvider frames,
        RenderOptions options,
        int depth,
        Rational frameRate)
    {
        LayerNode? Side(Clip clip, int lane)
        {
            if (!clip.Enabled)
            {
                return null;
            }

            DriverScope.Origin = clip.Start;
            Flicks local = time - clip.Start;
            Flicks shown = TransitionTiming.ClampToSource(project, clip, time);
            if (Source(project, clip, shown, lane, frameSize, output, frames, options, depth, frameRate) is not { } source)
            {
                return null;
            }

            return Layer(clip, local, source, frameSize, options, Steady(project, clip, shown, source.Size, frames, options)) with { Effects = Effects(clip, track, local, time, sequence, options) };
        }

        Transition transition = span.Transition;
        EffectDescriptor descriptor = options.Effects.Find(transition.TypeId) is { Kind: EffectKind.Transition } known
            ? known
            : new EffectDescriptor(transition.TypeId, EffectKind.Transition, transition.TypeId, "Unknown", string.Empty, []);

        Flicks into = time - span.Range.Start;
        Effect model = transition.AsEffect();
        ParameterSet parameters = ParameterSet.Evaluate(descriptor, model, into);
        float linear = span.Progress(time);

        var effect = new EffectNode(descriptor, parameters)
        {
            InstanceId = transition.Id,
            LocalTime = into,
            Seed = StableSeed(transition.Id),
            Model = model,
            OwnerLength = span.Range.Duration,
            SequenceTime = time,
            FrameRate = frameRate,
        };

        var source = new TransitionLayerSource(
            Side(span.Left, track.Order),
            Side(span.Right, IncomingLane(track.Order)),
            new TransitionNode(effect, TransitionEasing.Apply(parameters, linear)) { Linear = linear });

        return new LayerNode(source, output.Width, output.Height, Matrix3x2.Identity, LayerNode.NoCrop, 1.0f, BlendMode.Normal, [], options.Scale);
    }

    /// <summary>
    /// A subtitle track at a time: a title layer for each place cues are showing, drawn by the
    /// title generator from the track's style (<see cref="SubtitleLook"/>).
    /// </summary>
    private static IEnumerable<LayerNode> Subtitles(Track track, Flicks time, ProjectSettings settings, Vector2 frameSize, RenderOptions options)
    {
        if (options.Effects.Find(TitleParams.GeneratorId) is not { Kind: EffectKind.Generator, Implementation: { } type } descriptor
            || !typeof(VideoGenerator).IsAssignableFrom(type))
        {
            yield break;
        }

        foreach ((Clip first, Effect title) in SubtitleLook.Titles(track, time, settings.Width, settings.Height))
        {
            Flicks local = time - first.Start;
            var node = new EffectNode(descriptor, ParameterSet.Evaluate(descriptor, title, local))
            {
                InstanceId = first.Id,
                LocalTime = local,
                Seed = StableSeed(first.Id),
                Model = title,
                OwnerLength = first.Duration,
                SequenceTime = time,
                FrameRate = settings.FrameRate,
            };

            yield return Layer(first, local, (new GeneratorLayerSource(node), frameSize, ConformPolicy.Stretch), frameSize, options, Matrix3x2.Identity);
        }
    }

    /// <summary>What a picture clip shows, and how big it is in its own pixels.</summary>
    private static (LayerSource Source, Vector2 Size, ConformPolicy Policy)? Source(
        Project project,
        Clip clip,
        Flicks time,
        int lane,
        Vector2 frameSize,
        (int Width, int Height) output,
        IFrameProvider frames,
        RenderOptions options,
        int depth,
        Rational frameRate)
    {
        if (clip.MediaId is { } mediaId)
        {
            if (frames.Frame(project, clip, time, lane) is not { } frame)
            {
                // A missing file shows its slate, drawn by the title generator, not a gap.
                return frames.Offline(project, clip) is { } label
                    && options.Effects.Find(TitleParams.GeneratorId) is { Kind: EffectKind.Generator, Implementation: { } titleType } title
                    && typeof(VideoGenerator).IsAssignableFrom(titleType)
                    ? (new GeneratorLayerSource(new EffectNode(title, ParameterSet.Evaluate(title, OfflineSlate.Title(label, (int)frameSize.X, (int)frameSize.Y), Flicks.Zero))
                    {
                        InstanceId = clip.Id + ":offline",
                        LocalTime = Flicks.Zero,
                        Seed = StableSeed(clip.Id),
                        Model = OfflineSlate.Title(label, (int)frameSize.X, (int)frameSize.Y),
                        OwnerLength = clip.Duration,
                        SequenceTime = time,
                        FrameRate = frameRate,
                    }), frameSize, ConformPolicy.Stretch)
                    : null;
            }

            ConformPolicy policy = project.MediaItem(mediaId)?.Conform ?? ConformPolicy.Fit;
            Vector2 size = frame.Width > 0 && frame.Height > 0
                ? new Vector2(frame.Width, frame.Height)
                : new Vector2(frame.Frame.Width, frame.Frame.Height);

            if (Blended(project, clip, time, lane, frame, size, output, frames, options, frameRate) is { } blended)
            {
                return (blended, size, policy);
            }

            return (new FrameLayerSource(frame.Frame, frame.Color, frame.Identity), size, policy);
        }

        if (clip.SequenceId is { } sequenceId)
        {
            if (depth >= options.MaxNesting || project.Sequence(sequenceId) is not { } nested)
            {
                return null;
            }

            // The nested sequence plays its own time: where the clip's source in and speed put it,
            // less any of its tracks the clip leaves out.
            if (!clip.HiddenTracks.IsEmpty)
            {
                nested = nested with { Tracks = [.. nested.Tracks.Where(track => !clip.HiddenTracks.Contains(track.Id))] };
            }

            RenderGraph inner = Build(project, nested, clip.SourceTimeAt(time), frames, options, depth + 1);
            ProjectSettings innerSettings = project.SettingsFor(nested);
            return (new NestedLayerSource(inner), new Vector2(innerSettings.Width, innerSettings.Height), ConformPolicy.Fit);
        }

        if (clip.GeneratorId is { } generatorId
            && !string.Equals(generatorId, SolidGenerator, StringComparison.Ordinal)
            && options.Effects.Find(generatorId) is { Kind: EffectKind.Generator, Implementation: { } type } descriptor
            && typeof(VideoGenerator).IsAssignableFrom(type))
        {
            Flicks local = time - clip.Start;
            Effect? own = clip.Effects.FirstOrDefault(effect => EffectChains.IsOwnParameters(clip, effect));
            var node = new EffectNode(descriptor, ParameterSet.Evaluate(descriptor, own, local))
            {
                InstanceId = clip.Id,
                LocalTime = local,
                Seed = StableSeed(clip.Id),
                Model = own,
                OwnerLength = clip.Duration,
                SequenceTime = time,
                FrameRate = frameRate,
            };

            return (new GeneratorLayerSource(node), frameSize, ConformPolicy.Stretch);
        }

        if (string.Equals(clip.GeneratorId, SolidGenerator, StringComparison.Ordinal))
        {
            Vector4 colour = GeneratorColour(clip, time - clip.Start, options);

            return (new SolidLayerSource(new Vector4(colour.X * colour.W, colour.Y * colour.W, colour.Z * colour.W, colour.W)), frameSize, ConformPolicy.Stretch);
        }

        return null;
    }

    /// <summary>
    /// A retimed clip's picture between two source frames: the frame showing and the one after,
    /// crossfaded by how far between them the moment is (<see cref="RetimeMode.Blend"/>). Null when
    /// the clip shows its nearest frame, the moment is on a frame, or there is no frame after.
    /// </summary>
    /// <remarks>
    /// Both frames are drawn to the output's size and mixed by the crossfade transition, and the
    /// mix is then placed as the clip's picture: a clip scaled up past the frame is blended at the
    /// frame's resolution. The frame after is asked for as a freeze frame of that source frame on
    /// the same decoder lane, so playing forwards decodes each frame once.
    /// </remarks>
    private static TransitionLayerSource? Blended(
        Project project,
        Clip clip,
        Flicks time,
        int lane,
        SourceFrame shown,
        Vector2 size,
        (int Width, int Height) output,
        IFrameProvider frames,
        RenderOptions options,
        Rational frameRate)
    {
        if (clip.Retime == RetimeMode.Nearest
            || clip.IsHold
            || SourceRate(project, clip) is not { } rate
            || options.Effects.Find(Crossfade) is not { Kind: EffectKind.Transition } crossfade)
        {
            return null;
        }

        // Where the moment falls in source frames. A reversed clip shows the frame before the
        // position, as the frame server does, and blends towards the one after it.
        Flicks position = clip.SourceTimeAt(time);
        if (position < Flicks.Zero)
        {
            return null;
        }

        Int128 scaled = (Int128)position.Value * rate.Num;
        Int128 perFrame = (Int128)rate.Den * Flicks.PerSecond;
        Int128 whole = scaled / perFrame;
        float between = (float)((double)(scaled - (whole * perFrame)) / (double)perFrame);
        long frame = (long)whole - (clip.Reverse ? 1 : 0);
        if (frame < 0 || between < 1.0f / 512.0f)
        {
            return null;
        }

        Flicks half = new(Flicks.FromFrames(1, rate).Value / 2);
        Clip after = clip with
        {
            Hold = true,
            Remap = null,
            Speed = null,
            SourceIn = Flicks.FromFrames(frame + 1 + (clip.Reverse ? 1 : 0), rate) + half,
        };

        if (frames.Frame(project, after, time, lane) is not { } next)
        {
            return null;
        }

        LayerNode Side(SourceFrame picture) => new(
            new FrameLayerSource(picture.Frame, picture.Color, picture.Identity),
            (int)size.X,
            (int)size.Y,
            Matrix3x2.CreateScale(output.Width / size.X, output.Height / size.Y),
            LayerNode.NoCrop,
            1.0f,
            BlendMode.Normal,
            [],
            options.Scale);

        var effect = new EffectNode(crossfade, ParameterSet.Evaluate(crossfade, null, Flicks.Zero))
        {
            InstanceId = clip.Id + ":retime",
            LocalTime = Flicks.Zero,
            Seed = StableSeed(clip.Id),
            OwnerLength = clip.Duration,
            SequenceTime = time,
            FrameRate = frameRate,
        };

        return new TransitionLayerSource(Side(shown), Side(next), new TransitionNode(effect, between));
    }

    /// <summary>
    /// Where a clip's own picture lands at a moment: the matrix from its source pixels to the
    /// frame's, both from the top left, as the renderer places it. A media clip's picture is its
    /// stream's size, fitted as its media says; anything else is the frame's size, stretched.
    /// </summary>
    public static Matrix3x2 SourcePlacement(Project project, Clip clip, Flicks local, Vector2 frameSize)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(clip);

        (Vector2 size, ConformPolicy policy) = clip.MediaId is { } mediaId
            && project.MediaItem(mediaId) is { } item
            && item.Info?.Streams.FirstOrDefault(stream => stream.Index == clip.SourceStreamIndex) is { Width: > 0, Height: > 0 } stream
                ? (new Vector2(stream.Width, stream.Height), item.Conform)
                : (frameSize, ConformPolicy.Stretch);
        Transform transform = clip.Transform ?? Transform.Identity;

        return Placement(
            size,
            frameSize,
            policy,
            Float2(transform.Position, Intrinsic.Position, local),
            Float2(transform.Scale, Intrinsic.Scale, local),
            Float(transform.Rotation, Intrinsic.Rotation, local),
            Float2(transform.Anchor, Intrinsic.Anchor, local),
            1.0f);
    }

    /// <summary>
    /// Where a frame-sized picture (a generator's) of a clip lands at a moment: the matrix from
    /// its pixels to the frame's, both from the top left, with the clip's transform evaluated as
    /// the renderer evaluates it.
    /// </summary>
    public static Matrix3x2 FramePlacement(Clip clip, Flicks local, Vector2 frameSize)
    {
        ArgumentNullException.ThrowIfNull(clip);
        Transform transform = clip.Transform ?? Transform.Identity;

        return Placement(
            frameSize,
            frameSize,
            ConformPolicy.Stretch,
            Float2(transform.Position, Intrinsic.Position, local),
            Float2(transform.Scale, Intrinsic.Scale, local),
            Float(transform.Rotation, Intrinsic.Rotation, local),
            Float2(transform.Anchor, Intrinsic.Anchor, local),
            1.0f);
    }

    /// <summary>
    /// A track matte at a moment: the matte track drawn on its own, even when it is hidden, at the
    /// same size and time. Null for no matte, a track that is not there, or nesting too deep.
    /// </summary>
    private static TrackMatteNode? Matted(Project project, Sequence sequence, TrackMatte? matte, Flicks time, IFrameProvider frames, RenderOptions options, int depth)
    {
        if (matte is null
            || depth >= options.MaxNesting
            || sequence.Track(matte.SourceTrackId) is not { Kind: TrackKind.Video } source)
        {
            return null;
        }

        Sequence alone = sequence with { Tracks = EquatableArray.Create(source with { Muted = false, Matte = null }) };
        return new TrackMatteNode(Build(project, alone, time, frames, options, depth + 1), matte.Mode);
    }

    /// <summary>
    /// A clip with motion blur whose placement moves: the layer at each moment across the shutter,
    /// to be averaged. Null when no blur applies, the clip does not move, or every moment lands in
    /// the same place, so a still layer draws exactly as it would without blur.
    /// </summary>
    /// <remarks>
    /// The source is the frame at the frame's own time, placed at each moment; a generator is
    /// drawn again at each moment, so a title animating its own parameters is blurred too.
    /// </remarks>
    private static LayerNode? Blurred(
        Project project,
        Sequence sequence,
        Track track,
        Clip clip,
        Flicks time,
        (LayerSource Source, Vector2 Size, ConformPolicy Policy) source,
        LayerNode layer,
        Vector2 frameSize,
        (int Width, int Height) output,
        IFrameProvider frames,
        RenderOptions options,
        int depth,
        Rational frameRate)
    {
        if (options.MaxBlurSamples <= 1 || MotionBlur.For(clip, track, sequence) is not { } blur || !Moves(clip))
        {
            return null;
        }

        // A generator is drawn again at each moment only when its picture changes: its own
        // parameters animate, or it changes by itself (particles, a countdown). Otherwise every
        // moment places the one picture, and the compositor draws it once.
        bool redraw = source.Source is GeneratorLayerSource generator
            && (typeof(ITimedGenerator).IsAssignableFrom(generator.Node.Descriptor.Implementation) || OwnParametersAnimate(clip));
        IReadOnlyList<Flicks> moments = blur.Moments(time, frameRate, options.MaxBlurSamples);
        if (!redraw && moments.Count > 2)
        {
            LayerNode At(Flicks at) => Layer(clip, at - clip.Start, source, frameSize, options, Steady(project, clip, at, source.Size, frames, options));
            int needed = SamplesFor(At(moments[0]), At(moments[moments.Count / 2]), At(moments[^1]));
            if (needed < moments.Count)
            {
                moments = blur.Moments(time, frameRate, needed);
            }
        }

        var samples = ImmutableArray.CreateBuilder<LayerNode>();
        foreach (Flicks at in moments)
        {
            (LayerSource Source, Vector2 Size, ConformPolicy Policy) picture = redraw
                && Source(project, clip, at, track.Order, frameSize, output, frames, options, depth, frameRate) is { } redrawn
                    ? redrawn
                    : source;
            samples.Add(Layer(clip, at - clip.Start, picture, frameSize, options, Steady(project, clip, at, picture.Size, frames, options))
                with { Effects = layer.Effects, Blend = BlendMode.Normal });
        }

        LayerNode first = samples[0];
        if (!redraw && samples.All(sample => sample.Transform == first.Transform && sample.Crop == first.Crop && sample.Opacity == first.Opacity && sample.Masks.SequenceEqual(first.Masks)))
        {
            return null;
        }

        return new LayerNode(new MotionBlurLayerSource(samples.ToImmutable()), output.Width, output.Height, Matrix3x2.Identity, LayerNode.NoCrop, 1.0f, clip.BlendMode, [], options.Scale);
    }

    /// <summary>
    /// A clip with an echo: the layer at this frame and at earlier ones, spaced as the effect says,
    /// to be averaged with weights that fade with age. Null when the clip has no echo, or no
    /// earlier frame is inside it yet.
    /// </summary>
    private static LayerNode? Echoed(
        Project project,
        Track track,
        Clip clip,
        Flicks time,
        (LayerSource Source, Vector2 Size, ConformPolicy Policy) source,
        LayerNode layer,
        Vector2 frameSize,
        (int Width, int Height) output,
        IFrameProvider frames,
        RenderOptions options,
        int depth,
        Rational frameRate)
    {
        if (clip.Effects.IsEmpty
            || clip.Effects.FirstOrDefault(effect => effect.Enabled && string.Equals(effect.TypeId, EchoEffect.TypeId, StringComparison.Ordinal)) is not { } effect
            || options.Effects.Find(EchoEffect.TypeId) is not { } descriptor)
        {
            return null;
        }

        ParameterSet parameters = ParameterSet.Evaluate(descriptor, effect, time - clip.Start);
        int spacing = parameters.Int(EchoEffect.Spacing);
        float[] weights = EchoEffect.Weights(parameters.Int(EchoEffect.Count), parameters.Float(EchoEffect.Decay));
        var samples = ImmutableArray.CreateBuilder<LayerNode>();
        var shares = ImmutableArray.CreateBuilder<float>();
        for (int index = 0; index < weights.Length; index++)
        {
            Flicks at = time - Flicks.FromFrames(index * spacing, frameRate);
            if (at < clip.Start)
            {
                break;
            }

            (LayerSource Source, Vector2 Size, ConformPolicy Policy)? picture = index == 0
                ? source
                : Source(project, clip, at, track.Order, frameSize, output, frames, options, depth, frameRate);
            if (picture is not { } drawn)
            {
                break;
            }

            samples.Add(Layer(clip, at - clip.Start, drawn, frameSize, options, Steady(project, clip, at, drawn.Size, frames, options))
                with { Effects = layer.Effects, Blend = BlendMode.Normal });
            shares.Add(weights[index]);
        }

        if (samples.Count <= 1)
        {
            return null;
        }

        // The frames before the clip's start are missing, so what is there shares the whole weight.
        float sum = shares.Sum();
        for (int index = 0; index < shares.Count; index++)
        {
            shares[index] /= sum;
        }

        return new LayerNode(new MotionBlurLayerSource(samples.ToImmutable(), shares.ToImmutable()), output.Width, output.Height, Matrix3x2.Identity, LayerNode.NoCrop, 1.0f, clip.BlendMode, [], options.Scale);
    }

    /// <summary>True when something the compositor places a clip by is keyframed: what motion blur blurs.</summary>
    private static bool Moves(Clip clip)
    {
        Transform transform = clip.Transform ?? Transform.Identity;
        return transform.Position.IsAnimated
            || transform.Scale.IsAnimated
            || transform.Rotation.IsAnimated
            || transform.Anchor.IsAnimated
            || clip.Crop is { } crop && (crop.Left.IsAnimated || crop.Top.IsAnimated || crop.Right.IsAnimated || crop.Bottom.IsAnimated)
            || clip.Masks.Any(mask => mask.Bounds?.IsAnimated == true || mask.PathData?.IsAnimated == true || mask.Feather?.IsAnimated == true || mask.Expansion?.IsAnimated == true)
            || OwnParametersAnimate(clip);
    }

    /// <summary>True when a generator clip animates its own parameters (a title's fly in, a shape's size).</summary>
    private static bool OwnParametersAnimate(Clip clip) =>
        clip.GeneratorId is not null && clip.Effects.Any(effect => EffectChains.IsOwnParameters(clip, effect) && effect.Parameters.Any(parameter => parameter.Value.IsAnimated));

    /// <summary>
    /// How many moments a layer's blur needs: one for every output pixel its farthest corner
    /// travels across the shutter (through the middle moment, so a turn inside it counts), and
    /// at least two. Samples closer than a pixel apart look the same as more of them, so a slow
    /// layer costs a few draws rather than the setting's full count.
    /// </summary>
    private static int SamplesFor(LayerNode first, LayerNode middle, LayerNode last)
    {
        float travel = 0;
        foreach (Vector2 corner in (ReadOnlySpan<Vector2>)[Vector2.Zero, new(first.SourceWidth, 0), new(0, first.SourceHeight), new(first.SourceWidth, first.SourceHeight)])
        {
            Vector2 start = Vector2.Transform(corner, first.Transform);
            Vector2 through = Vector2.Transform(corner, middle.Transform);
            Vector2 end = Vector2.Transform(corner, last.Transform);
            travel = MathF.Max(travel, Vector2.Distance(start, through) + Vector2.Distance(through, end));
        }

        return (int)Math.Clamp(MathF.Ceiling(travel) + 1, 2, MotionBlur.MaxSamples);
    }

    /// <summary>
    /// A stabilized clip's correction at a moment, as a matrix in its picture's pixels to go
    /// before its placement: the move and turn that put the camera back on its smoothed path, and
    /// the zoom that hides the edges. The identity when the clip is not stabilized or its motion has
    /// not been analysed.
    /// </summary>
    private static Matrix3x2 Steady(Project project, Clip clip, Flicks time, Vector2 size, IFrameProvider frames, RenderOptions options)
    {
        if (clip.Effects.IsEmpty
            || clip.Effects.FirstOrDefault(effect => effect.Enabled && string.Equals(effect.TypeId, StabilizeEffect.TypeId, StringComparison.Ordinal)) is not { } effect
            || options.Effects.Find(StabilizeEffect.TypeId) is not { } descriptor
            || SourceRate(project, clip) is not { } rate
            || frames.Motion(project, clip) is not { Count: > 0, Width: > 0, Height: > 0 } motion)
        {
            return Matrix3x2.Identity;
        }

        ParameterSet parameters = ParameterSet.Evaluate(descriptor, effect, time - clip.Start);
        int smoothing = parameters.Int(StabilizeEffect.Smoothing);
        StabilizeCorrection correction = motion.Correction(SourceFrameAt(clip, time, rate), smoothing);

        float zoom = 1.0f + (parameters.Float(StabilizeEffect.Zoom) / 100.0f);
        if (parameters.Bool(StabilizeEffect.AutoZoom))
        {
            zoom *= motion.CoveringZoom(SourceFrameAt(clip, clip.Start, rate), SourceFrameAt(clip, clip.End - new Flicks(1), rate), smoothing);
        }

        var ratio = new Vector2(size.X / motion.Width, size.Y / motion.Height);
        Vector2 centre = size / 2.0f;
        return Matrix3x2.CreateTranslation(correction.Shift * ratio)
            * Matrix3x2.CreateTranslation(-centre)
            * Matrix3x2.CreateRotation(correction.Angle)
            * Matrix3x2.CreateScale(zoom)
            * Matrix3x2.CreateTranslation(centre);
    }

    /// <summary>The nominal frame rate of a media clip's video stream, or null when it has none.</summary>
    private static Rational? SourceRate(Project project, Clip clip) =>
        clip.MediaId is { } mediaId
        && project.MediaItem(mediaId)?.Info?.Streams.FirstOrDefault(stream => stream.Index == clip.SourceStreamIndex)?.FrameRate is { Num: > 0 } rate
            ? rate
            : null;

    /// <summary>The source frame a media clip shows at a moment, from zero, as the frame server picks it.</summary>
    private static long SourceFrameAt(Clip clip, Flicks time, Rational rate)
    {
        Flicks position = clip.SourceTimeAt(time);
        long frame = position < Flicks.Zero ? 0 : (long)((Int128)position.Value * rate.Num / ((Int128)rate.Den * Flicks.PerSecond));
        return Math.Max(0, frame - (clip.Reverse ? 1 : 0));
    }

    private static LayerNode Layer(
        Clip clip,
        Flicks local,
        (LayerSource Source, Vector2 Size, ConformPolicy Policy) source,
        Vector2 frameSize,
        RenderOptions options,
        Matrix3x2 steady)
    {
        Transform transform = clip.Transform ?? Transform.Identity;

        Matrix3x2 placement = steady * Placement(
            source.Size,
            frameSize,
            source.Policy,
            Float2(transform.Position, Intrinsic.Position, local),
            Float2(transform.Scale, Intrinsic.Scale, local),
            Float(transform.Rotation, Intrinsic.Rotation, local),
            Float2(transform.Anchor, Intrinsic.Anchor, local),
            options.Scale);

        return new LayerNode(
            source.Source,
            (int)source.Size.X,
            (int)source.Size.Y,
            placement,
            CropRect(clip.Crop, local),
            Float(clip.Opacity, Intrinsic.Opacity, local),
            clip.BlendMode,
            Mattes(clip.Masks, local),
            options.Scale);
    }

    /// <summary>
    /// A clip's effects then its track's, evaluated: the clip's at clip time, the track's at
    /// sequence time. A generator's own parameters, disabled effects and types the registry does
    /// not have as picture effects are left out.
    /// </summary>
    private static ImmutableArray<EffectNode> Effects(Clip clip, Track track, Flicks local, Flicks time, Sequence sequence, RenderOptions options)
    {
        if (clip.Effects.IsEmpty && track.Effects.IsEmpty)
        {
            return [];
        }

        var nodes = ImmutableArray.CreateBuilder<EffectNode>();
        Flicks sequenceLength = track.Effects.IsEmpty ? Flicks.Zero : sequence.Duration;

        foreach (Effect effect in clip.Effects)
        {
            // Stabilizing moves the layer (see Steady) and echo draws it again at earlier frames
            // (see Echoed), rather than either running over its picture.
            if (!string.Equals(effect.TypeId, clip.GeneratorId, StringComparison.Ordinal)
                && !string.Equals(effect.TypeId, StabilizeEffect.TypeId, StringComparison.Ordinal)
                && !string.Equals(effect.TypeId, EchoEffect.TypeId, StringComparison.Ordinal)
                && Node(effect, local, clip.Duration, time, options) is { } node)
            {
                nodes.Add(node);
            }
        }

        // A track's effects are timed from the sequence's start, a clip's from its own.
        DriverScope.Origin = Flicks.Zero;
        foreach (Effect effect in track.Effects)
        {
            if (Node(effect, time, sequenceLength, time, options) is { } node)
            {
                nodes.Add(node);
            }
        }
        DriverScope.Origin = clip.Start;

        return nodes.ToImmutable();
    }

    private static EffectNode? Node(Effect effect, Flicks time, Flicks ownerLength, Flicks sequenceTime, RenderOptions options)
    {
        if (!effect.Enabled || options.Effects.Find(effect.TypeId) is not { Kind: EffectKind.Video } descriptor)
        {
            return null;
        }

        return new EffectNode(descriptor, ParameterSet.Evaluate(descriptor, effect, time))
        {
            InstanceId = effect.Id,
            LocalTime = time,
            Seed = StableSeed(effect.Id),
            Model = effect,
            Masks = Mattes(effect.Masks, time),
            OwnerLength = ownerLength,
            SequenceTime = sequenceTime,
        };
    }

    /// <summary>
    /// FNV-1a over the identifier: the same seed on every run and every machine, which
    /// <see cref="string.GetHashCode()"/> is not.
    /// </summary>
    internal static int StableSeed(string id)
    {
        uint hash = 2166136261;
        foreach (char character in id)
        {
            hash = (hash ^ character) * 16777619;
        }

        return unchecked((int)hash);
    }

    private static LayerNode Adjustment(Clip clip, Flicks local, Vector2 frameSize, RenderOptions options)
    {
        // An adjustment layer's masks are drawn in frame pixels, so its picture is the frame.
        Matrix3x2 frame = Matrix3x2.CreateScale(options.Scale);

        return new LayerNode(
            new SolidLayerSource(Vector4.Zero),
            (int)frameSize.X,
            (int)frameSize.Y,
            frame,
            LayerNode.NoCrop,
            Float(clip.Opacity, Intrinsic.Opacity, local),
            clip.BlendMode,
            Mattes(clip.Masks, local),
            options.Scale)
        {
            IsAdjustment = true,
        };
    }

    private static ImmutableArray<MatteShape> Mattes(EquatableArray<Mask> masks, Flicks local)
    {
        if (masks.IsEmpty)
        {
            return [];
        }

        var shapes = ImmutableArray.CreateBuilder<MatteShape>();
        foreach (Mask mask in masks)
        {
            if (!mask.Enabled)
            {
                continue;
            }

            shapes.Add(new MatteShape(
                mask.Shape,
                Float4(mask.Bounds, Intrinsic.MaskBounds, local),
                Text(mask.PathData, Intrinsic.MaskPath, local),
                Float(mask.Feather, Intrinsic.MaskFeather, local),
                Float(mask.Opacity, Intrinsic.MaskOpacity, local),
                mask.Mode,
                mask.Invert,
                Float(mask.Expansion, Intrinsic.MaskExpansion, local)));
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

        float left = Float(crop.Left, Intrinsic.CropLeft, local) / 100.0f;
        float top = Float(crop.Top, Intrinsic.CropTop, local) / 100.0f;
        float right = Float(crop.Right, Intrinsic.CropRight, local) / 100.0f;
        float bottom = Float(crop.Bottom, Intrinsic.CropBottom, local) / 100.0f;

        return new Vector4(left, top, MathF.Max(left, 1.0f - right), MathF.Max(top, 1.0f - bottom));
    }

    private static Vector4 GeneratorColour(Clip clip, Flicks local, RenderOptions options)
    {
        EffectDescriptor? solid = options.Effects.Find(SolidGenerator);
        Effect? parameters = null;
        foreach (Effect effect in clip.Effects)
        {
            if (string.Equals(effect.TypeId, clip.GeneratorId, StringComparison.Ordinal))
            {
                parameters = effect;
                break;
            }
        }

        return solid is null
            ? new Vector4(0.5f, 0.5f, 0.5f, 1.0f)
            : ParameterSet.Evaluate(solid, parameters, local).Color("color");
    }

    // Every intrinsic value goes through ParamEval, as effect parameters do: defaults, limits and
    // hand-edited types are handled in one place, and so will Phase 29a's drivers be.
    private static float Float(AnimatedValue? value, ParamDescriptor descriptor, Flicks time) =>
        ParamEval.Eval(value, descriptor, time) is ParamValue.Float result ? result.Value : 0.0f;

    private static Vector2 Float2(AnimatedValue? value, ParamDescriptor descriptor, Flicks time) =>
        ParamEval.Eval(value, descriptor, time) is ParamValue.Float2 result ? result.Value : Vector2.Zero;

    private static Vector4 Float4(AnimatedValue? value, ParamDescriptor descriptor, Flicks time) =>
        ParamEval.Eval(value, descriptor, time) is ParamValue.Float4 result ? result.Value : Vector4.Zero;

    private static string Text(AnimatedValue? value, ParamDescriptor descriptor, Flicks time) =>
        ParamEval.Eval(value, descriptor, time) switch
        {
            ParamValue.Path path => path.Value,
            ParamValue.Text text => text.Value,
            _ => string.Empty,
        };

    /// <summary>The descriptors of a clip's and a mask's own parameters, looked up once.</summary>
    private static class Intrinsic
    {
        public static readonly ParamDescriptor Position = ParamTargets.Transform.Param("transform.position")!;
        public static readonly ParamDescriptor Scale = ParamTargets.Transform.Param("transform.scale")!;
        public static readonly ParamDescriptor Rotation = ParamTargets.Transform.Param("transform.rotation")!;
        public static readonly ParamDescriptor Anchor = ParamTargets.Transform.Param("transform.anchor")!;
        public static readonly ParamDescriptor Opacity = ParamTargets.Opacity.Param("opacity")!;
        public static readonly ParamDescriptor CropLeft = ParamTargets.Crop.Param("crop.left")!;
        public static readonly ParamDescriptor CropTop = ParamTargets.Crop.Param("crop.top")!;
        public static readonly ParamDescriptor CropRight = ParamTargets.Crop.Param("crop.right")!;
        public static readonly ParamDescriptor CropBottom = ParamTargets.Crop.Param("crop.bottom")!;
        public static readonly ParamDescriptor MaskBounds = ParamTargets.MaskParams.Param("bounds")!;
        public static readonly ParamDescriptor MaskPath = ParamTargets.MaskParams.Param("path")!;
        public static readonly ParamDescriptor MaskFeather = ParamTargets.MaskParams.Param("feather")!;
        public static readonly ParamDescriptor MaskOpacity = ParamTargets.MaskParams.Param("opacity")!;
        public static readonly ParamDescriptor MaskExpansion = ParamTargets.MaskParams.Param("expansion")!;
    }
}
