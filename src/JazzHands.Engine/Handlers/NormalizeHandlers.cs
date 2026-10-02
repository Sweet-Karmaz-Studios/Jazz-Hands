using System.Globalization;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Audio;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>Sets a clip's, a track's or the mix's gain so it measures a target.</summary>
/// <remarks>The sound is mixed and measured before the command is queued.</remarks>
public sealed class NormalizeAudioHandler : ICommandHandler<NormalizeAudioCommand>, IPreparingHandler<NormalizeAudioCommand>
{
    private const double MaxGain = 24.0;

    /// <inheritdoc />
    public object? Prepare(Project project, NormalizeAudioCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        AudioSubject subject = AudioSubject.Resolve(project, command.ClipId, command.TrackId, command.Mix, command.SequenceId);
        return new PreparedWork(Key(project, subject), AudioMeasure.Measure(project, subject, context.ProjectPath, context.Cancellation));
    }

    /// <summary>What a measurement is of: the sequence as it is, which the mix is made from, and the media.</summary>
    private static (Sequence, EquatableArray<MediaItem>) Key(Project project, AudioSubject subject) => (subject.Sequence, project.Media);

    /// <inheritdoc />
    public Project Handle(Project project, NormalizeAudioCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        double target = command.Target ?? NormalizeAudioCommand.DefaultTarget(command.Mode);
        if (target is > 0 or < -70)
        {
            throw new CommandException("invalid-value", string.Create(CultureInfo.InvariantCulture, $"The target is from -70 to 0, not {target}."), "target");
        }

        AudioSubject subject = AudioSubject.Resolve(project, command.ClipId, command.TrackId, command.Mix, command.SequenceId);
        AudioLevels levels = PreparedWork.Reuse(context, Key(project, subject), () => AudioMeasure.Measure(project, subject, context.ProjectPath, context.Cancellation));
        double measured = levels.Level(command.Mode)
            ?? throw new CommandException("silent", "It is silent there, or too quiet to measure, so there is no gain that would reach a level.");
        double change = Math.Round(target - measured, 2);
        string unit = command.Mode == NormalizeMode.Lufs ? "LUFS" : "dBFS";

        if (subject.IsMix)
        {
            Sequence sequence = subject.Sequence;
            MasterBus master = sequence.Master ?? new MasterBus();
            context.Changed(sequence.Id);
            return project.ReplaceSequence(sequence with { Master = master with { Volume = Shift(master.Volume, change, measured, target, unit) } });
        }

        if (subject.TrackId is { } trackId)
        {
            (Sequence _, Track track) = HandlerHelp.Track(project, trackId);
            HandlerHelp.RequireUnlocked(track);
            context.Changed(track.Id);
            return project.ReplaceTrack(track with { Volume = Shift(track.Volume, change, measured, target, unit) });
        }

        foreach (string clipId in subject.ClipIds)
        {
            ClipLocation found = HandlerHelp.Clip(project, clipId);
            HandlerHelp.RequireUnlocked(found.Track);
            context.Changed(clipId);
            project = project.ReplaceTrack(found.Track.ReplaceClip(found.Clip with { Volume = Shift(found.Clip.Volume, change, measured, target, unit) }));
        }

        return project;
    }

    /// <summary>A volume in decibels moved by a change: a fixed value, or every keyframe of it, so its shape is kept.</summary>
    private static AnimatedValue Shift(AnimatedValue? volume, double change, double measured, double target, string unit)
    {
        switch (volume)
        {
            case null:
                return AnimatedValue.Constant(Checked(0, change, measured, target, unit));

            case StaticValue { Value: ParamValue.Float level }:
                return AnimatedValue.Constant(Checked(level.Value, change, measured, target, unit));

            case KeyframedValue keyed:
                return keyed with
                {
                    Keyframes = EquatableArray.Create([.. keyed.Keyframes.Select(key => key with
                    {
                        Value = new ParamValue.Float(Checked(key.Value is ParamValue.Float at ? at.Value : 0, change, measured, target, unit)),
                    })]),
                };

            default:
                throw new CommandException("param-driven", "The volume is driven by an expression; clear the driver first, or change its expression.");
        }
    }

    private static float Checked(float current, double change, double measured, double target, string unit)
    {
        double wanted = current + change;
        if (wanted > MaxGain)
        {
            throw new CommandException(
                "gain-out-of-range",
                string.Create(CultureInfo.InvariantCulture, $"It measures {measured:0.0} {unit}: reaching {target:0.#} {unit} needs {change:+0.0;-0.0} dB, which takes the volume to {wanted:+0.0;-0.0} dB, past +{MaxGain} dB."));
        }

        return (float)Math.Max(wanted, -144.0);
    }
}

/// <summary>Measures a clip, a track or the mix on its own.</summary>
public sealed class MeasureAudioHandler : IQueryHandler<MeasureAudioQuery, AudioMeasurement>
{
    /// <inheritdoc />
    public AudioMeasurement Handle(Project project, MeasureAudioQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        AudioSubject subject = AudioSubject.Resolve(project, query.ClipId, query.TrackId, query.Mix, query.SequenceId);
        AudioLevels levels = AudioMeasure.Measure(project, subject, context.Session?.ProjectPath ?? string.Empty);
        return new AudioMeasurement(
            subject.Name,
            levels.From,
            levels.To,
            Round(levels.Level(NormalizeMode.Peak)),
            Round(levels.Level(NormalizeMode.Rms)),
            Round(levels.Level(NormalizeMode.Lufs)));
    }

    private static double? Round(double? value) => value is { } level ? Math.Round(level, 2) : null;
}
