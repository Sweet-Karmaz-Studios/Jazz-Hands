using System.Numerics;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Render.Compositing;

namespace JazzHands.Engine.Handlers;

/// <summary>Makes a vertical version of a sequence.</summary>
public sealed class ReframeSequenceHandler : ICommandHandler<ReframeSequenceCommand>
{
    /// <summary>Frames either side a followed point is averaged over, so the window pans like a camera operator rather than jittering with the track.</summary>
    public const int Smoothing = 6;

    /// <inheritdoc />
    public Project Handle(Project project, ReframeSequenceCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence source = HandlerHelp.Sequence(project, command.FromSequenceId);
        Flicks length = source.Duration;
        if (length <= Flicks.Zero)
        {
            throw new CommandException("empty-sequence", $"'{source.Name}' has nothing in it to reframe.");
        }

        FrameSize size = command.Size ?? new FrameSize(1080, 1920);
        if (size.Width < 16 || size.Height < 16 || command.Window is < 0.1 or > 10 || command.Blur is < 0 or > 1000)
        {
            throw new CommandException("invalid-value", "The size is at least 16 each way, the window's shape 0.1 to 10, and the blur 0 to 1000 pixels.");
        }

        ProjectSettings original = project.SettingsFor(source);
        ProjectSettings settings = original with { Width = size.Width, Height = size.Height };
        var sourceSize = new Vector2(original.Width, original.Height);
        var frame = new Vector2(size.Width, size.Height);
        string name = string.IsNullOrWhiteSpace(command.Name) ? $"{source.Name} vertical" : command.Name;

        // Behind: the original covering the frame, blurred and darker.
        var background = new Clip(Id.New(), new TimeRange(Flicks.Zero, length), Flicks.Zero, SequenceId: source.Id, Name: $"{source.Name} (background)")
        {
            Transform = Transform.Identity with { Scale = Uniform(Cover(sourceSize, frame) / Fit(sourceSize, frame)) },
            Effects =
            [
                .. command.Blur > 0 ? [Effect.Create("video.blur.gaussian").WithParameter("radius", AnimatedValue.Constant((float)command.Blur))] : Array.Empty<Effect>(),
                Effect.Create("color.basic").WithParameter("exposure", AnimatedValue.Constant(-1.0f)),
            ],
        };

        Project result = project;
        Clip front;
        Track[] lifted = [];
        ReframeSource? reframed = null;
        if (command.Mode == ReframeMode.Fit)
        {
            front = new Clip(Id.New(), new TimeRange(Flicks.Zero, length), Flicks.Zero, SequenceId: source.Id, Name: source.Name);
        }
        else
        {
            // Graphics (titles, shapes, adjustment layers) are laid over the vertical frame rather
            // than cut by the window: hidden in the nests, and copied above, fitted to the width.
            Track[] graphics = [.. source.Tracks.Where(IsGraphics)];
            string[] hidden = [.. graphics.Select(track => track.Id)];
            background = background with { HiddenTracks = [.. hidden] };
            float across = frame.X / sourceSize.X;
            float down = (frame.X / (float)command.Window) / sourceSize.Y;
            reframed = new ReframeSource(source.Id, across, down);
            lifted = [.. graphics.Select((track, index) => Lift(track, across, down) with
            {
                Id = Id.New(),
                Order = 2 + index,
                Lifted = new LiftedTrack(track.Id, across, down),
            })];

            // The window: a sequence the shape of the window, the original's height, holding the
            // original scaled to cover it; that clip's position is the pan.
            int windowHeight = original.Height;
            int windowWidth = Math.Max(16, (int)Math.Round(windowHeight * command.Window / 2) * 2);
            var window = new Vector2(windowWidth, windowHeight);
            var pan = new Clip(Id.New(), new TimeRange(Flicks.Zero, length), Flicks.Zero, SequenceId: source.Id, Name: source.Name)
            {
                Transform = Transform.Identity with
                {
                    Scale = Uniform(Cover(sourceSize, window) / Fit(sourceSize, window)),
                    Position = command.Follow is { } trackId
                        ? Follow(project, source, trackId, sourceSize, window)
                        : AnimatedValue.Constant(new ParamValue.Float2(Vector2.Zero)),
                },
                HiddenTracks = [.. hidden],
            };

            var crop = new Sequence(Id.New(), $"{source.Name} crop", [new Track(Id.New(), TrackKind.Video, "V1", 0, [pan])])
            {
                Settings = original with { Width = windowWidth, Height = windowHeight },
            };
            context.Changed(crop.Id);
            result = result.AddSequence(crop);
            front = new Clip(Id.New(), new TimeRange(Flicks.Zero, length), Flicks.Zero, SequenceId: crop.Id, Name: crop.Name);
        }

        // The sound, copied: a nested sequence carries its picture, not its sound.
        Track[] sound =
        [
            .. source.Tracks
                .Where(track => track.Kind == TrackKind.Audio)
                .Select((track, index) => track with
                {
                    Id = Id.New(),
                    Order = 2 + lifted.Length + index,
                    Clips = [.. track.Clips.Select(clip => clip with { Id = Id.New(), LinkGroupId = null })],
                    Transitions = [],
                }),
        ];

        var vertical = new Sequence(
            Id.New(),
            name,
            [
                new Track(Id.New(), TrackKind.Video, "V1", 0, [background]),
                new Track(Id.New(), TrackKind.Video, "V2", 1, [front]),
                .. lifted,
                .. sound,
            ])
        {
            Settings = settings,
            Reframed = reframed,
        };

        context.Changed(vertical.Id);
        context.Changed(lifted.SelectMany(track => track.Clips.Select(clip => clip.Id)));
        context.Changed(project.Id);
        return result.AddSequence(vertical) with { ActiveSequenceId = vertical.Id };
    }

    /// <summary>
    /// The pan that keeps a tracked point in the middle of the window: the point on the original
    /// frame, averaged over a few frames either side, moved to the window's middle and held so the
    /// window never runs past the picture's edge.
    /// </summary>
    private static AnimatedValue Follow(Project project, Sequence source, string trackId, Vector2 sourceSize, Vector2 window)
    {
        (ClipLocation tracked, PointTrack track) = TrackingHelp.Find(project, trackId);
        if (tracked.Sequence.Id != source.Id)
        {
            throw new CommandException("other-sequence", "The point track is on a clip in another sequence; follow one tracked in the sequence being reframed.", "follow");
        }

        if (track.Points.IsEmpty)
        {
            throw new CommandException("track-empty", "The track has no points.", "follow");
        }

        // Where the point is on the original's frame, from its middle, frame by frame.
        float[] across =
        [
            .. track.Points.Select(point =>
                Vector2.Transform(new Vector2((float)point.X, (float)point.Y), RenderGraphBuilder.SourcePlacement(project, tracked.Clip, point.Time, sourceSize)).X - (sourceSize.X / 2)),
        ];

        float scale = Cover(sourceSize, window);
        float reach = MathF.Max(0, ((sourceSize.X * scale) - window.X) / 2);
        var keyframes = new List<Keyframe>(across.Length);
        for (int index = 0; index < across.Length; index++)
        {
            int first = Math.Max(0, index - Smoothing);
            int last = Math.Min(across.Length - 1, index + Smoothing);
            float mean = across[first..(last + 1)].Average();
            float x = Math.Clamp(-mean * scale, -reach, reach);
            Flicks at = tracked.Clip.Start + track.Points[index].Time;
            keyframes.Add(new Keyframe(at, new ParamValue.Float2(MathF.Round(x, 2), 0), Interp.Linear));
        }

        return new KeyframedValue(keyframes);
    }

    /// <summary>A track that only draws: no file and no nested sequence on it, and something to draw.</summary>
    internal static bool IsGraphics(Track track) =>
        track.Kind is TrackKind.Video or TrackKind.Adjustment
        && !track.Clips.IsEmpty
        && track.Clips.All(clip => clip.MediaId is null && clip.SequenceId is null);

    /// <summary>
    /// A graphics track copied into the vertical frame: every clip new, its places across fitted
    /// to the width and down spread over the window, and its sizes fitted to the width, so nothing
    /// is cut off and nothing that sat side by side overlaps. The track keeps the original's id and
    /// order, for the caller to set; <paramref name="idFor"/> gives a clip's id from the original's,
    /// a new one when it gives null.
    /// </summary>
    internal static Track Lift(Track track, float across, float down, Func<string, string?>? idFor = null)
    {
        Vector2 Place(Vector2 point) => new(MathF.Round(point.X * across, 2), MathF.Round(point.Y * down, 2));

        AnimatedValue? Map(AnimatedValue? value, Func<ParamValue, ParamValue> change) => value switch
        {
            StaticValue fixedValue => new StaticValue(change(fixedValue.Value)),
            KeyframedValue keyed => new KeyframedValue(keyed.Keyframes.Select(key => key with { Value = change(key.Value) })),
            DrivenValue driven => driven with { Base = Map(driven.Base, change)! },
            _ => value,
        };

        ParamValue Point(ParamValue value) => value is ParamValue.Float2 pair ? new ParamValue.Float2(Place(pair.Value)) : value;

        ParamValue Size(ParamValue value) => value switch
        {
            ParamValue.Float number => new ParamValue.Float(MathF.Round(number.Value * across, 3)),
            ParamValue.Float2 pair => new ParamValue.Float2(pair.Value * across),
            _ => value,
        };

        Clip Moved(Clip clip)
        {
            Clip copy = clip with { Id = idFor?.Invoke(clip.Id) ?? Id.New(), LinkGroupId = null };
            if (copy.Transform is { } transform)
            {
                copy = copy with { Transform = transform with { Position = Map(transform.Position, Point)! } };
            }

            // A generator's own sizes and places in pixels.
            if (copy.GeneratorId is { } generator && Effects.EffectCatalog.Registry.Find(generator) is { } descriptor)
            {
                copy = copy with
                {
                    Effects = [.. copy.Effects.Select(effect => !EffectChains.IsOwnParameters(copy, effect)
                        ? effect with { Id = Id.New() }
                        : effect with
                        {
                            Parameters = [.. effect.Parameters.Select(parameter => descriptor.Param(parameter.Name) is { Unit: "px" } param
                                ? parameter with { Value = Map(parameter.Value, param.Type == ParamType.Point || parameter.Name == "position" ? Point : Size)! }
                                : parameter)],
                        })],
                };
            }

            return copy;
        }

        return track with
        {
            Clips = [.. track.Clips.Select(Moved)],
            Transitions = [],
        };
    }

    /// <summary>The scale that makes a picture cover a frame, both ways.</summary>
    private static float Cover(Vector2 picture, Vector2 frame) => MathF.Max(frame.X / picture.X, frame.Y / picture.Y);

    /// <summary>The scale the frame fits a nested picture with, which the clip's own scale multiplies.</summary>
    private static float Fit(Vector2 picture, Vector2 frame) => MathF.Min(frame.X / picture.X, frame.Y / picture.Y);

    private static AnimatedValue Uniform(float scale) => AnimatedValue.Constant(new ParamValue.Float2(new Vector2(MathF.Round(scale, 5))));
}
