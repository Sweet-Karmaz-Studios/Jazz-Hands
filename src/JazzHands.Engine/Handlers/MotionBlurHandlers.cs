using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>What the motion blur handlers share: checking and building a setting.</summary>
internal static class MotionBlurHelp
{
    /// <summary>The setting a command asks for, on top of what was there; null to inherit.</summary>
    internal static MotionBlur? Setting(MotionBlur? was, double? angle, int? samples, bool off, bool inherit)
    {
        if (off && inherit)
        {
            throw new CommandException("invalid-value", "Choose --off or --inherit, not both.", "off");
        }

        if (angle is { } degrees && (!double.IsFinite(degrees) || degrees <= 0 || degrees > MotionBlur.MaxAngle))
        {
            throw new CommandException("invalid-value", FormattableString.Invariant($"The shutter angle is more than 0 and at most {MotionBlur.MaxAngle} degrees."), "angle");
        }

        if (samples is { } count && (count < 2 || count > MotionBlur.MaxSamples))
        {
            throw new CommandException("invalid-value", FormattableString.Invariant($"Motion blur averages 2 to {MotionBlur.MaxSamples} moments."), "samples");
        }

        if (inherit)
        {
            return null;
        }

        MotionBlur basis = was ?? new MotionBlur();
        return basis with
        {
            ShutterAngle = angle ?? basis.ShutterAngle,
            Samples = samples ?? basis.Samples,
            Enabled = !off,
        };
    }
}

/// <summary>Sets a clip's motion blur.</summary>
public sealed class SetClipMotionBlurHandler : ICommandHandler<SetClipMotionBlurCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetClipMotionBlurCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);
        MotionBlur? blur = MotionBlurHelp.Setting(found.Clip.MotionBlur, command.Angle, command.Samples, command.Off, command.Inherit);
        if (blur == found.Clip.MotionBlur)
        {
            return project;
        }

        context.Changed(found.Clip.Id);
        return project.ReplaceTrack(found.Track.ReplaceClip(found.Clip with { MotionBlur = blur }));
    }
}

/// <summary>Sets a track's motion blur.</summary>
public sealed class SetTrackMotionBlurHandler : ICommandHandler<SetTrackMotionBlurCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTrackMotionBlurCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (_, Track track) = HandlerHelp.Track(project, command.TrackId);
        MotionBlur? blur = MotionBlurHelp.Setting(track.MotionBlur, command.Angle, command.Samples, command.Off, command.Inherit);
        if (blur == track.MotionBlur)
        {
            return project;
        }

        context.Changed(track.Id);
        return project.ReplaceTrack(track with { MotionBlur = blur });
    }
}

/// <summary>Sets a sequence's motion blur.</summary>
public sealed class SetSequenceMotionBlurHandler : ICommandHandler<SetSequenceMotionBlurCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetSequenceMotionBlurCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);

        // A sequence has nothing above it: off is no setting at all.
        MotionBlur? blur = command.Off ? null : MotionBlurHelp.Setting(sequence.MotionBlur, command.Angle, command.Samples, off: false, inherit: false);
        if (blur == sequence.MotionBlur)
        {
            return project;
        }

        context.Changed(sequence.Id);
        return project.ReplaceSequence(sequence with { MotionBlur = blur });
    }
}
