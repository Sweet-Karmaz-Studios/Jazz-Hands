using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Vfx;
using JazzHands.Render.Effects;

namespace JazzHands.Engine.Handlers;

/// <summary>Places a preset's effects timed to one moment.</summary>
public sealed class ApplyVfxPresetHandler : ICommandHandler<ApplyVfxPresetCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ApplyVfxPresetCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        VfxPreset preset = VfxPresets.Find(command.Preset)
            ?? throw new CommandException("unknown-preset", $"There is no preset '{command.Preset}'. There are {string.Join(", ", VfxPresets.All.Select(known => known.Name))}.");
        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);
        Flicks moment = Moment(sequence, command);

        if (command.To is { } target)
        {
            ParamOwner owner = ParamTargets.Find(project, target) is { Kind: ParamOwnerKind.Clip or ParamOwnerKind.Track } found
                ? found
                : throw new CommandException("target-not-found", $"No clip or track has the id '{target}'.", "to");
            HandlerHelp.RequireUnlocked(owner.Track);
            if (owner.Track.Kind is not (TrackKind.Video or TrackKind.Adjustment))
            {
                throw new CommandException("not-picture", "These effects go on a picture: a video or adjustment clip or track.", "to");
            }

            if (owner.Clip is { } clip && (moment < clip.Start || moment >= clip.End))
            {
                throw new CommandException("time-out-of-range", $"The moment is not inside '{clip.Name}'.", "at");
            }

            Effect[] effects = Effects(preset, moment - owner.Origin, context);
            context.Changed(owner.Id);
            return owner.Clip is { } on
                ? project.ReplaceTrack(owner.Track.ReplaceClip(on with { Effects = [.. on.Effects, .. effects] }))
                : project.ReplaceTrack(owner.Track with { Effects = [.. owner.Track.Effects, .. effects] });
        }

        // On a new adjustment clip around the moment, on the topmost adjustment track with room.
        Flicks start = Flicks.Max(Flicks.Zero, moment - Flicks.FromSeconds(preset.Before));
        Flicks end = moment + Flicks.FromSeconds(preset.After);
        var adjustment = new Clip(
            Id.New(),
            new TimeRange(start, end - start),
            Flicks.Zero,
            GeneratorId: "gen.solid",
            Name: preset.Title,
            Effects: [.. Effects(preset, moment - start, context)]);

        Track? free = sequence.Tracks
            .Where(track => track.Kind == TrackKind.Adjustment && !track.Locked && !EditOps.Overlaps(track, adjustment))
            .OrderByDescending(track => track.Order)
            .FirstOrDefault();
        context.Changed(adjustment.Id);
        if (free is not null)
        {
            context.Changed(free.Id);
            return project.ReplaceTrack(free.AddClip(adjustment));
        }

        var added = new Track(Id.New(), TrackKind.Adjustment, HandlerHelp.TrackName(sequence, TrackKind.Adjustment), sequence.NextTrackOrder());
        context.Changed(added.Id);
        context.Changed(sequence.Id);
        return project.ReplaceSequence(sequence.AddTrack(added.AddClip(adjustment)));
    }

    /// <summary>The moment of the hit: <c>--at</c>, or a marker's time.</summary>
    private static Flicks Moment(Sequence sequence, ApplyVfxPresetCommand command)
    {
        if (command.At is not null && command.Marker is not null)
        {
            throw new CommandException("invalid-value", "Give the moment with --at or --marker, not both.", "marker");
        }

        if (command.Marker is { } name)
        {
            Marker marker = sequence.Markers.FirstOrDefault(candidate => candidate.Id == name)
                ?? sequence.Markers.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new CommandException("marker-not-found", $"No marker on '{sequence.Name}' has the name or id '{name}'.", "marker");
            return marker.Time;
        }

        return command.At is { } at
            ? at >= Flicks.Zero ? at : throw new CommandException("time-out-of-range", "The moment is before the start of the sequence.", "at")
            : throw new CommandException("invalid-value", "Say when the hit is, with --at or --marker.", "at");
    }

    /// <summary>The preset's effects with their triggers on the moment, in the owner's time.</summary>
    private static Effect[] Effects(VfxPreset preset, Flicks trigger, HandlerContext context)
    {
        EffectRegistry registry = VideoEffects.Registry;
        var effects = new Effect[preset.Effects.Length];
        for (int index = 0; index < effects.Length; index++)
        {
            PresetEffect planned = preset.Effects[index];
            EffectDescriptor descriptor = registry.Find(planned.TypeId)!;
            Effect effect = Effect.Create(planned.TypeId);
            if (descriptor.Param("trigger") is { } when)
            {
                effect = effect.WithParameter("trigger", new StaticValue(ParamValues.Parse(when, trigger.ToSeconds().ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
            }

            foreach ((string name, string value) in planned.Values)
            {
                effect = effect.WithParameter(name, new StaticValue(ParamValues.Parse(descriptor.Param(name)!, value)));
            }

            foreach ((string name, (double after, string value)[] keys) in planned.Keys ?? [])
            {
                ParamDescriptor param = descriptor.Param(name)!;
                Keyframe[] frames = [.. keys
                    .Select(key => new Keyframe(Flicks.Max(Flicks.Zero, trigger + Flicks.FromSeconds(key.after)), ParamValues.Parse(param, key.value), Interp.EaseOut))
                    .GroupBy(key => key.Time)
                    .Select(same => same.Last())];
                effect = effect.WithParameter(name, new KeyframedValue(frames));
            }

            context.Changed(effect.Id);
            effects[index] = effect;
        }

        return effects;
    }
}

/// <summary>Lists the effect presets.</summary>
public sealed class ListVfxPresetsHandler : IQueryHandler<ListVfxPresetsQuery, VfxPresetInfo[]>
{
    /// <inheritdoc />
    public VfxPresetInfo[] Handle(Project project, ListVfxPresetsQuery query, QueryContext context) =>
        [.. VfxPresets.All.Select(preset => new VfxPresetInfo(
            preset.Name,
            preset.Description,
            [.. preset.Effects.Select(effect => effect.TypeId)],
            Flicks.FromSeconds(preset.Before),
            Flicks.FromSeconds(preset.After)))];
}
