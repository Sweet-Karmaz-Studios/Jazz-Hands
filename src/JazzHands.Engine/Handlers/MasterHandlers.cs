using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>Sets a sequence's master volume.</summary>
public sealed class SetMasterVolumeHandler : ICommandHandler<SetMasterVolumeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetMasterVolumeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);
        float db = AudioHelp.Gain(command.Db);
        MasterBus master = sequence.Master ?? new MasterBus();

        // The master's volume is the same kind of value as a track's, so it keyframes the same way.
        ParamDescriptor descriptor = ParamTargets.Audio.Params[0];
        AnimatedValue? volume;
        if (command.At is { } at)
        {
            if (at.IsNegative)
            {
                throw new CommandException("time-out-of-range", $"{Timecode.FormatClock(at)} is before the sequence starts.");
            }

            Flicks tolerance = new(Flicks.FromFrames(1, project.SettingsFor(sequence).FrameRate).Value / 2);
            volume = ParamHelp.Upsert(master.Volume as KeyframedValue, descriptor, at, new ParamValue.Float(db), interp: null, tolerance);
        }
        else if (master.Volume is KeyframedValue { IsAnimated: true })
        {
            throw new CommandException(
                "param-animated",
                $"The master volume of '{sequence.Name}' has keyframes, so it has no one value to set. Give --at to set the keyframe there.");
        }
        else
        {
            volume = db == 0.0f ? null : AnimatedValue.Constant(db);
        }

        return MasterHelp.Store(project, sequence, master with { Volume = volume }, context);
    }
}

/// <summary>Turns a sequence's master limiter on or off, or sets its ceiling.</summary>
public sealed class SetMasterLimiterHandler : ICommandHandler<SetMasterLimiterCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetMasterLimiterCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (command.On is null && command.Ceiling is null)
        {
            throw new CommandException("nothing-to-change", "Give --on, --ceiling or both.");
        }

        if (command.Ceiling is { } ceiling && !(ceiling is >= -24.0 and <= 0.0))
        {
            throw new CommandException("value-out-of-range", $"A ceiling of {ceiling} dBTP is not one this will set. Use -24 to 0; -1 is usual.");
        }

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);
        MasterBus master = sequence.Master ?? new MasterBus();

        // On and -1 are the defaults, so they are stored as no value, and a master that is all
        // defaults is no master at all.
        bool? limiter = command.On is { } on ? (on ? null : false) : master.Limiter;
        double? stored = command.Ceiling is { } given ? (given == MasterBus.DefaultCeiling ? null : given) : master.Ceiling;

        return MasterHelp.Store(project, sequence, master with { Limiter = limiter, Ceiling = stored }, context);
    }
}

/// <summary>What the master commands share.</summary>
internal static class MasterHelp
{
    /// <summary>Stores a master bus, as nothing when it is all defaults, and reports the sequence.</summary>
    internal static Project Store(Project project, Sequence sequence, MasterBus master, HandlerContext context)
    {
        MasterBus? stored = master.IsDefault ? null : master;
        if (stored == sequence.Master)
        {
            return project;
        }

        context.Changed(sequence.Id);
        return project.ReplaceSequence(sequence with { Master = stored });
    }
}
