using System.Numerics;
using JazzHands.Core.Animation;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Media.Analysis;
using JazzHands.Render.Compositing;

namespace JazzHands.Engine.Handlers;

/// <summary>What the tracking handlers share: finding a track and its clip.</summary>
internal static class TrackingHelp
{
    /// <summary>The track with an id, with the clip it is on.</summary>
    internal static (ClipLocation Clip, PointTrack Track) Find(Project project, string trackId)
    {
        foreach (Sequence sequence in project.Sequences)
        {
            foreach (Track track in sequence.Tracks)
            {
                foreach (Clip clip in track.Clips)
                {
                    if (clip.PointTracks.FirstOrDefault(point => point.Id == trackId) is { } found)
                    {
                        return (project.FindClip(clip.Id)!, found);
                    }
                }
            }
        }

        throw new CommandException("track-not-found", $"No point track with id '{trackId}'. tracking.point makes one.");
    }
}

/// <summary>Tracks a point through a clip's frames.</summary>
public sealed class TrackPointHandler : ICommandHandler<TrackPointCommand>
{
    /// <summary>A match below this, and the point is taken as lost.</summary>
    public const double Lost = 0.5;

    /// <inheritdoc />
    public Project Handle(Project project, TrackPointCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);
        Clip clip = found.Clip;
        if (clip.MediaId is not { } mediaId || found.Track.Kind != TrackKind.Video)
        {
            throw new CommandException("not-media", "Only a clip of a video file has a picture to track.");
        }

        if (clip.Reverse || clip.IsRemapped || clip.IsHold)
        {
            throw new CommandException("not-plain", "A point is tracked in a clip playing forwards at one speed: not reversed, remapped or frozen.");
        }

        if (command.Size is < 5 or > 201 || command.Search is < 1 or > 500)
        {
            throw new CommandException("invalid-value", "The square followed is 5 to 201 pixels, and the search 1 to 500.", "size");
        }

        if (!Enum.IsDefined(command.Direction))
        {
            throw new CommandException("invalid-value", $"{(int)command.Direction} is not a direction.", "direction");
        }

        Flicks local = command.At - clip.Start;
        if (local < Flicks.Zero || local >= clip.Duration)
        {
            throw new CommandException("time-out-of-range", "Pick the point at a moment inside the clip.", "at");
        }

        MediaItem item = MediaServices.Require(project, mediaId);
        MediaStream stream = item.Info?.Streams.FirstOrDefault(candidate => candidate.Index == clip.SourceStreamIndex && candidate.Kind == MediaStreamKind.Video) is { FrameRate: { Num: > 0 } } video
            ? video
            : throw new CommandException("not-media", $"'{item.Name}' has no video to track.");
        string path = HandlerHelp.Resolve(context, item.RelativePath);
        if (!File.Exists(path))
        {
            throw new CommandException("media-missing", $"'{item.Name}' is not at {path}. Relink it first.");
        }

        Rational rate = stream.FrameRate!.Value;
        double speed = clip.EffectiveSpeed.ToDouble();
        long start = clip.SourceTimeAt(command.At).ToFrames(rate, RoundingMode.Floor);
        long firstFrame = clip.SourceIn.ToFrames(rate, RoundingMode.Ceiling);
        long lastFrame = clip.SourceOut.ToFrames(rate, RoundingMode.Ceiling) - 1;
        Flicks LocalOf(long frame) => new((long)Math.Round((Flicks.FromFrames(frame, rate) - clip.SourceIn).Value / speed));

        var points = new SortedDictionary<long, TrackPoint>();
        var picked = new Vector2((float)command.X, (float)command.Y);
        using (var reader = new LumaReader(path))
        {
            LumaImage origin = reader.Read(Flicks.FromFrames(start, rate))
                ?? throw new CommandException("time-out-of-range", "There is no frame of the file there.", "at");
            points[start] = new TrackPoint(LocalOf(start), command.X, command.Y, 1);

            if (command.Direction != TrackDirection.Backward)
            {
                Follow(reader, new PointTracker(origin, picked, command.Size, command.Search), start + 1, lastFrame, 1);
            }

            if (command.Direction != TrackDirection.Forward)
            {
                Follow(reader, new PointTracker(origin, picked, command.Size, command.Search), start - 1, firstFrame, -1);
            }
        }

        void Follow(LumaReader reader, PointTracker tracker, long from, long to, int step)
        {
            for (long frame = from; step > 0 ? frame <= to : frame >= to; frame += step)
            {
                if (reader.Read(Flicks.FromFrames(frame, rate)) is not { } image)
                {
                    return;
                }

                TrackedPoint point = tracker.Next(image);
                if (point.Confidence < Lost)
                {
                    return;
                }

                points[frame] = new TrackPoint(LocalOf(frame), Math.Round(point.Position.X, 3), Math.Round(point.Position.Y, 3), Math.Round(point.Confidence, 3));
            }
        }

        // Re-tracking keeps what is on the other side of the corrected point.
        PointTrack? existing = command.TrackId is { } id ? clip.PointTracks.FirstOrDefault(track => track.Id == id) : null;
        IEnumerable<TrackPoint> kept = existing is null
            ? []
            : existing.Points.Where(point => command.Direction switch
            {
                TrackDirection.Forward => point.Time < local,
                TrackDirection.Backward => point.Time > local,
                _ => false,
            });

        TrackPoint[] merged = [.. kept.Concat(points.Values).GroupBy(point => point.Time).Select(group => group.Last()).OrderBy(point => point.Time)];
        PointTrack track = existing is { } before
            ? before with { Size = command.Size, Points = [.. merged] }
            : new PointTrack(command.TrackId is { } fresh ? CommandValues.ParseId(fresh) : Id.New(), command.Name ?? $"Track {clip.PointTracks.Length + 1}", command.Size, [.. merged]);

        EquatableArray<PointTrack> tracks = existing is null
            ? clip.PointTracks.Add(track)
            : [.. clip.PointTracks.Select(candidate => candidate.Id == track.Id ? track : candidate)];

        context.Changed(clip.Id);
        context.Changed(track.Id);
        return project.ReplaceTrack(found.Track.ReplaceClip(clip with { PointTracks = tracks }));
    }
}

/// <summary>Makes something follow a tracked point.</summary>
public sealed class ApplyTrackHandler : ICommandHandler<ApplyTrackCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ApplyTrackCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (ClipLocation tracked, PointTrack track) = TrackingHelp.Find(project, command.TrackId);
        if (track.Points.IsEmpty)
        {
            throw new CommandException("track-empty", "The track has no points.");
        }

        ParamOwner owner = ParamTargets.Find(project, command.To)
            ?? throw new CommandException("target-not-found", $"Nothing has the id '{command.To}'.", "to");
        HandlerHelp.RequireUnlocked(owner.Track);

        return owner.Kind == ParamOwnerKind.Mask
            ? Mask(project, owner, tracked.Clip, track, context)
            : Point(project, owner, tracked, track, command, context);
    }

    /// <summary>A clip's position or an effect's point, in sequence pixels from the frame centre.</summary>
    private static Project Point(Project project, ParamOwner owner, ClipLocation tracked, PointTrack track, ApplyTrackCommand command, HandlerContext context)
    {
        EffectRegistry registry = Effects.EffectCatalog.Registry;
        string name = command.Param ?? owner.Kind switch
        {
            ParamOwnerKind.Clip => "transform.position",
            ParamOwnerKind.Effect when ParamTargets.Param(owner, "target", registry) is not null => "target",
            _ => throw new CommandException("invalid-value", "Name the parameter that follows with --param.", "param"),
        };

        ParamDescriptor descriptor = ParamTargets.Param(owner, name, registry)
            ?? throw new CommandException("unknown-param", $"There is no parameter '{name}' there.", "param");
        if (descriptor.Type is not (ParamType.Point or ParamType.Float2))
        {
            throw new CommandException("invalid-value", $"'{name}' is not a position, so it cannot follow a point.", "param");
        }

        ProjectSettings settings = project.SettingsFor(tracked.Sequence);
        var frame = new Vector2(settings.Width, settings.Height);
        Vector2 OnFrame(TrackPoint point) =>
            Vector2.Transform(new Vector2((float)point.X, (float)point.Y), RenderGraphBuilder.SourcePlacement(project, tracked.Clip, point.Time, frame)) - (frame / 2);

        TrackPoint first = track.Points[0];
        Vector2 anchor = OnFrame(first);
        Flicks firstLocal = tracked.Clip.Start + first.Time - owner.Origin;
        Vector2 start = ParamEval.Eval(ParamTargets.Get(owner, name), descriptor, firstLocal) is ParamValue.Float2 pair ? pair.Value : Vector2.Zero;

        var keyframes = new List<Keyframe>();
        foreach (TrackPoint point in track.Points)
        {
            Flicks at = tracked.Clip.Start + point.Time - owner.Origin;
            if (at < Flicks.Zero || at > owner.Length)
            {
                continue;
            }

            Vector2 value = command.Absolute ? OnFrame(point) : start + (OnFrame(point) - anchor);
            keyframes.Add(new Keyframe(at, new ParamValue.Float2(MathF.Round(value.X, 2), MathF.Round(value.Y, 2)), Interp.Linear));
        }

        if (keyframes.Count == 0)
        {
            throw new CommandException("time-out-of-range", "None of the track falls where that lasts.", "to");
        }

        context.Changed(owner.Id);
        return ParamHelp.Store(project, owner, descriptor, new KeyframedValue(keyframes), context);
    }

    /// <summary>A mask on the tracked clip, its shape moved with the point in the clip's own pixels.</summary>
    private static Project Mask(Project project, ParamOwner owner, Clip tracked, PointTrack track, HandlerContext context)
    {
        if (owner.Clip?.Id != tracked.Id)
        {
            throw new CommandException("other-clip", "A mask follows a point tracked on its own clip.", "to");
        }

        Mask mask = owner.Mask!;
        bool box = mask.Shape is MaskShape.Rectangle or MaskShape.Ellipse;
        string name = box ? "bounds" : "path";
        ParamDescriptor descriptor = ParamTargets.MaskParams.Param(name)!;
        TrackPoint first = track.Points[0];
        ParamValue start = ParamEval.Eval(ParamTargets.Get(owner, name), descriptor, first.Time);

        var keyframes = new List<Keyframe>();
        foreach (TrackPoint point in track.Points)
        {
            var by = new Vector2((float)(point.X - first.X), (float)(point.Y - first.Y));
            ParamValue? value = start switch
            {
                ParamValue.Float4 bounds => new ParamValue.Float4(bounds.Value + new Vector4(by, 0, 0)),
                ParamValue.Path outline when MaskShapes.Translate(outline.Value, by) is { } moved => new ParamValue.Path(moved),
                _ => null,
            };

            if (value is not null)
            {
                keyframes.Add(new Keyframe(point.Time, value, Interp.Linear));
            }
        }

        if (keyframes.Count == 0)
        {
            throw new CommandException("invalid-value", "The mask has no shape to move.", "to");
        }

        context.Changed(mask.Id);
        return ParamHelp.Store(project, owner, descriptor, new KeyframedValue(keyframes), context);
    }
}

/// <summary>Removes a point track.</summary>
public sealed class RemovePointTrackHandler : ICommandHandler<RemovePointTrackCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemovePointTrackCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (ClipLocation found, PointTrack track) = TrackingHelp.Find(project, command.TrackId);
        HandlerHelp.RequireUnlocked(found.Track);
        context.Changed(found.Clip.Id);
        return project.ReplaceTrack(found.Track.ReplaceClip(found.Clip with { PointTracks = [.. found.Clip.PointTracks.Where(candidate => candidate.Id != track.Id)] }));
    }
}
