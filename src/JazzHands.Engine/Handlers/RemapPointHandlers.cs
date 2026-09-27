using JazzHands.Core.Animation;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>
/// The speed curve by its points (Phase 45): what the timeline's speed lane sends. Each changes the
/// curve, then the clip's length follows, so the clip plays the same stretch of its source.
/// </summary>
internal static class RemapPoints
{
    /// <summary>The fastest a point may be, as the ramp command allows.</summary>
    internal const double Fastest = 100;

    /// <summary>A media clip, refusing anything else.</summary>
    internal static ClipLocation Clip(Project project, string clipId)
    {
        ClipLocation found = HandlerHelp.Clip(project, clipId);
        HandlerHelp.RequireUnlocked(found.Track);
        if (!found.Clip.IsMedia)
        {
            throw new CommandException("not-media", "Only a clip of a media file plays source at a speed.");
        }

        if (found.Clip.IsHold)
        {
            throw new CommandException("clip-held", "A freeze frame has no speed to shape.");
        }

        return found;
    }

    /// <summary>The curve's points, in time order; a curve that is one speed, or none, has none.</summary>
    internal static List<Keyframe> Points(Clip clip) =>
        clip.Remap is KeyframedValue keyed ? [.. keyed.Keyframes.OrderBy(key => key.Time)] : [];

    /// <summary>The speed of a curve with no points.</summary>
    internal static float Base(Clip clip) => clip.Remap switch
    {
        StaticValue { Value: ParamValue.Float value } => value.Value,
        _ => (float)clip.EffectiveSpeed.ToDouble(),
    };

    /// <summary>A point number from a command, from 1, checked.</summary>
    internal static int Index(List<Keyframe> points, int point) =>
        point >= 1 && point <= points.Count
            ? point - 1
            : throw new CommandException("invalid-value", points.Count == 0 ? "The clip's speed curve has no points yet." : $"The speed curve has points 1 to {points.Count}.", "point");

    /// <summary>A speed from a command, checked.</summary>
    internal static float Speed(double speed) =>
        double.IsFinite(speed) && speed is >= 0 and <= Fastest
            ? (float)speed
            : throw new CommandException("bad-speed", "Speeds run from 0 (a held frame) to 100.", "speed");

    /// <summary>A timeline time as a time in the clip, on the sequence's frame, inside it.</summary>
    internal static Flicks Local(Project project, ClipLocation found, Flicks at)
    {
        Flicks local = at.SnapToFrame(project.SettingsFor(found.Sequence).FrameRate, RoundingMode.Nearest) - found.Clip.Start;
        if (local < Flicks.Zero || local > found.Clip.Duration)
        {
            throw new CommandException("time-out-of-range", "A point of the speed curve goes inside the clip.", "at");
        }

        return local;
    }

    /// <summary>
    /// Puts a new curve on the clip and its linked clips and makes the clip as long as it needs to
    /// be to play the source it played before, on the sequence's frames: rippling what follows in
    /// a magnetic sequence, and elsewhere growing only into the room there is.
    /// </summary>
    internal static Project Apply(Project project, ClipLocation found, AnimatedValue remap, HandlerContext context)
    {
        Clip clip = found.Clip;
        Rational rate = project.SettingsFor(found.Sequence).FrameRate;

        // The source the clip plays up to: the end of the last source frame it shows, on its
        // file's frames. Kept, not re-measured from a rounded length, so edit after edit the clip
        // ends on the same source frame and never creeps into more of its source.
        Rational sourceRate = clip.MediaId is { } mediaId && project.MediaItem(mediaId)?.Info?.VideoStreams.FirstOrDefault()?.FrameRate is { IsZero: false } known ? known : rate;
        Flicks lastShown = (clip.SourceTimeAt(clip.End - Flicks.FromFrames(1, rate)) - clip.SourceIn).SnapToFrame(sourceRate);
        Flicks source = lastShown + Flicks.FromFrames(1, sourceRate);
        Flicks duration = LengthFor(remap, source, rate)
            ?? throw new CommandException("too-slow", "At that speed the clip would run for over an hour to play its source. Speed it up somewhere.");

        Clip[] partners = [.. found.Sequence.Tracks.SelectMany(track => track.Clips)
            .Where(candidate => candidate.Id == clip.Id || (clip.LinkGroupId is { } link && candidate.LinkGroupId == link))];
        string[] tracks = [.. found.Sequence.Tracks.Where(track => track.Clips.Any(candidate => partners.Contains(candidate))).Select(track => track.Id)];
        Flicks delta = duration - clip.Duration;
        Sequence sequence = found.Sequence;
        bool magnetic = sequence.Magnetic == true;

        if (!magnetic && delta > Flicks.Zero)
        {
            // Only as far as the next clip on each of the tracks.
            Flicks room = sequence.Tracks.Where(track => tracks.Contains(track.Id))
                .SelectMany(track => track.Clips.Where(other => !partners.Contains(other) && other.Start >= clip.End).Select(other => other.Start - clip.End))
                .DefaultIfEmpty(Flicks.MaxValue)
                .Min();
            delta = Flicks.Min(delta, room);
            duration = clip.Duration + delta;
        }

        if (magnetic && delta > Flicks.Zero)
        {
            sequence = HandlerContext.Require(EditOps.Ripple(sequence, clip.End, delta, tracks));
        }

        foreach (Clip partner in partners)
        {
            Track track = sequence.Tracks.First(candidate => candidate.Clip(partner.Id) is not null);
            Clip current = track.Clip(partner.Id)!;
            sequence = sequence.ReplaceTrack(track.ReplaceClip(current with { Remap = remap, Range = new TimeRange(current.Start, duration) }));
            context.Changed(partner.Id);
        }

        if (magnetic && delta < Flicks.Zero)
        {
            sequence = HandlerContext.Require(EditOps.Ripple(sequence, clip.End, delta, tracks));
        }

        return SequenceEdit.Commit(project, found.Sequence, sequence, context);
    }

    /// <summary>The fewest whole frames over which the curve plays a stretch of source (to a thousandth of a frame, the integration's error); null past an hour.</summary>
    internal static Flicks? LengthFor(AnimatedValue remap, Flicks source, Rational rate)
    {
        Flicks frame = Flicks.FromFrames(1, rate);
        Flicks enough = source - (frame / 1000);
        long low = 1;
        long high = Math.Max(1, rate.Num * 3600 / Math.Max(1, rate.Den));
        if (TimeRemap.Offset(remap, Flicks.FromFrames(high, rate)) < enough)
        {
            return null;
        }

        while (low < high)
        {
            long middle = low + ((high - low) / 2);
            if (TimeRemap.Offset(remap, Flicks.FromFrames(middle, rate)) >= enough)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return Flicks.FromFrames(low, rate);
    }
}

/// <summary>Adds a point to a speed curve.</summary>
public sealed class RemapAddPointHandler : ICommandHandler<RemapAddPointCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemapAddPointCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = RemapPoints.Clip(project, command.ClipId);
        Clip clip = found.Clip;
        Flicks local = RemapPoints.Local(project, found, command.At);
        AnimatedValue before = clip.Remap ?? AnimatedValue.Constant(RemapPoints.Base(clip));
        float speed = command.Speed is { } given ? RemapPoints.Speed(given) : (float)TimeRemap.Speed(before, local);

        List<Keyframe> points = RemapPoints.Points(clip);
        if (points.Count == 0 && local > Flicks.Zero)
        {
            // The curve starts at the speed it had, so the part before the new point is unchanged.
            points.Add(new Keyframe(Flicks.Zero, new ParamValue.Float(RemapPoints.Base(clip)), Interp.Linear));
        }

        int existing = points.FindIndex(point => point.Time == local);
        var added = new Keyframe(local, new ParamValue.Float(speed), existing >= 0 ? points[existing].Interp : Interp.Linear);
        if (existing >= 0)
        {
            points[existing] = added;
        }
        else
        {
            points.Add(added);
        }

        var curve = new KeyframedValue([.. points.OrderBy(point => point.Time)]);
        return curve == clip.Remap ? project : RemapPoints.Apply(project, found, curve, context);
    }
}

/// <summary>Moves a point of a speed curve.</summary>
public sealed class RemapMovePointHandler : ICommandHandler<RemapMovePointCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemapMovePointCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = RemapPoints.Clip(project, command.ClipId);
        List<Keyframe> points = RemapPoints.Points(found.Clip);
        int index = RemapPoints.Index(points, command.Point);
        Keyframe point = points[index];

        Flicks time = point.Time;
        if (command.At is { } at)
        {
            // Between its neighbours, a frame from each.
            Flicks frame = project.SettingsFor(found.Sequence).FrameDuration;
            Flicks earliest = index == 0 ? Flicks.Zero : points[index - 1].Time + frame;
            // The last may stay where it is when the clip has got shorter than it, but not go further.
            Flicks latest = index == points.Count - 1 ? Flicks.Max(found.Clip.Duration, point.Time) : points[index + 1].Time - frame;
            time = Flicks.Clamp(at.SnapToFrame(project.SettingsFor(found.Sequence).FrameRate, RoundingMode.Nearest) - found.Clip.Start, earliest, latest);
        }

        ParamValue value = command.Speed is { } speed ? new ParamValue.Float(RemapPoints.Speed(speed)) : point.Value;
        Keyframe moved = point with { Time = time, Value = value };
        if (moved == point)
        {
            return project;
        }

        points[index] = moved;
        return RemapPoints.Apply(project, found, new KeyframedValue([.. points]), context);
    }
}

/// <summary>Sets how a speed curve leaves a point.</summary>
public sealed class RemapSetEaseHandler : ICommandHandler<RemapSetEaseCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemapSetEaseCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (!Enum.IsDefined(command.Ease) || command.Ease == Interp.Bezier)
        {
            throw new CommandException("invalid-value", "The ease is linear, easeIn, easeOut, easeInOut or hold.", "ease");
        }

        ClipLocation found = RemapPoints.Clip(project, command.ClipId);
        List<Keyframe> points = RemapPoints.Points(found.Clip);
        int index = RemapPoints.Index(points, command.Point);
        if (points[index].Interp == command.Ease)
        {
            return project;
        }

        points[index] = points[index] with { Interp = command.Ease };
        return RemapPoints.Apply(project, found, new KeyframedValue([.. points]), context);
    }
}

/// <summary>Removes a point from a speed curve.</summary>
public sealed class RemapRemovePointHandler : ICommandHandler<RemapRemovePointCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemapRemovePointCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = RemapPoints.Clip(project, command.ClipId);
        List<Keyframe> points = RemapPoints.Points(found.Clip);
        int index = RemapPoints.Index(points, command.Point);
        Keyframe removed = points[index];
        points.RemoveAt(index);

        // The last point gone, the clip plays at the speed that point had.
        AnimatedValue curve = points.Count == 0 ? AnimatedValue.Constant(((ParamValue.Float)removed.Value).Value) : new KeyframedValue([.. points]);
        return RemapPoints.Apply(project, found, curve, context);
    }
}
