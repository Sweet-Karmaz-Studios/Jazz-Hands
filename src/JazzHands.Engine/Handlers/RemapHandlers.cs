using JazzHands.Core.Animation;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>What the remap handlers share: a clip and every clip linked to it get the same curve.</summary>
internal static class RemapHelp
{
    /// <summary>Sets the remap curve on a clip and its link partners.</summary>
    internal static Project Apply(Project project, ClipLocation found, AnimatedValue? remap, HandlerContext context)
    {
        Project updated = project;
        foreach (Track track in found.Sequence.Tracks)
        {
            foreach (Clip clip in track.Clips.Where(clip => clip.Id == found.Clip.Id || (found.Clip.LinkGroupId is { } link && clip.LinkGroupId == link)))
            {
                HandlerHelp.RequireUnlocked(track);
                Track current = updated.Sequence(found.Sequence.Id)!.Track(track.Id)!;
                updated = updated.ReplaceTrack(current.ReplaceClip(clip with { Remap = remap }));
                context.Changed(clip.Id);
            }
        }

        return updated;
    }
}

/// <summary>Turns time remapping on or off.</summary>
public sealed class SetClipRemapHandler : ICommandHandler<SetClipRemapCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetClipRemapCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        if (!found.Clip.IsMedia)
        {
            throw new CommandException("not-media", "Only a clip of a media file plays source at a speed.");
        }

        if (command.Off == !found.Clip.IsRemapped)
        {
            return project;
        }

        AnimatedValue? remap = command.Off ? null : AnimatedValue.Constant((float)found.Clip.EffectiveSpeed.ToDouble());
        return RemapHelp.Apply(project, found, remap, context);
    }
}

/// <summary>Puts a speed ramp on a clip's remap curve.</summary>
public sealed class RampClipSpeedHandler : ICommandHandler<RampClipSpeedCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RampClipSpeedCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        Clip clip = found.Clip;
        if (!clip.IsMedia)
        {
            throw new CommandException("not-media", "Only a clip of a media file plays source at a speed.");
        }

        if (command.From < 0 || command.To < 0 || command.From > 100 || command.To > 100)
        {
            throw new CommandException("bad-speed", "Speeds run from 0 (a held frame) to 100.", "from");
        }

        if (command.Duration <= Flicks.Zero)
        {
            throw new CommandException("bad-duration", "A ramp takes some time.", "dur");
        }

        Flicks start = command.At - clip.Start;
        Flicks end = start + command.Duration;
        if (start < Flicks.Zero || end > clip.Duration)
        {
            throw new CommandException("time-out-of-range", $"The ramp runs past the clip, which is {Timecode.FormatClock(clip.Duration)} long.", "at");
        }

        // What the curve is now, without anything inside the ramp.
        Keyframe[] kept = clip.Remap switch
        {
            KeyframedValue keyed => [.. keyed.Keyframes.Where(keyframe => keyframe.Time < start || keyframe.Time > end)],
            StaticValue { Value: ParamValue.Float value } when start > Flicks.Zero => [new Keyframe(Flicks.Zero, value, Interp.Hold)],
            null when start > Flicks.Zero => [new Keyframe(Flicks.Zero, new ParamValue.Float((float)clip.EffectiveSpeed.ToDouble()), Interp.Hold)],
            _ => [],
        };

        Interp shape = command.Linear ? Interp.Linear : Interp.EaseInOut;
        Keyframe[] ramp =
        [
            new Keyframe(start, new ParamValue.Float((float)command.From), shape),
            new Keyframe(end, new ParamValue.Float((float)command.To), Interp.Hold),
        ];

        return RemapHelp.Apply(project, found, new KeyframedValue(kept.Concat(ramp)), context);
    }
}
