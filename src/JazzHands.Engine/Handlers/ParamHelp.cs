using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Effects;

namespace JazzHands.Engine.Handlers;

/// <summary>
/// What the parameter, keyframe and effect handlers share: finding an owner, naming a parameter,
/// reading a time, finding a keyframe and storing the result.
/// </summary>
internal static class ParamHelp
{
    /// <summary>Whatever has an id, or a coded refusal.</summary>
    internal static ParamOwner Owner(Project project, string id) =>
        ParamTargets.Find(project, id)
        ?? throw new CommandException(
            "target-not-found",
            $"Nothing in this project has id '{id}'. Parameters belong to clips, tracks, effects, masks and transitions; 'jazz clip list', 'jazz effect get' and 'jazz transition list' show their ids.",
            "/sequences");

    /// <summary>An owner whose track is not locked.</summary>
    internal static ParamOwner Editable(Project project, string id)
    {
        ParamOwner owner = Owner(project, id);
        HandlerHelp.RequireUnlocked(owner.Track);
        return owner;
    }

    /// <summary>An effect, or a coded refusal.</summary>
    internal static ParamOwner Effect(Project project, string effectId)
    {
        ParamOwner owner = ParamTargets.Find(project, effectId) is { Kind: ParamOwnerKind.Effect } found
            ? found
            : throw new CommandException("effect-not-found", $"No effect with id '{effectId}'.", "/sequences");

        return owner;
    }

    /// <summary>A parameter of an owner, or a refusal that lists what it does have.</summary>
    internal static ParamDescriptor Param(ParamOwner owner, string name)
    {
        if (ParamTargets.Param(owner, name, EffectCatalog.Registry) is { } descriptor)
        {
            return descriptor;
        }

        // A plugin's own parameters (Phase 46) are p and their CLAP id, plain values the plugin clamps.
        if (owner.Kind == ParamOwnerKind.Effect && owner.Effect!.TypeId == JazzHands.Audio.Effects.PluginEffect.TypeId
            && name.StartsWith(JazzHands.Audio.Effects.PluginEffect.ParameterPrefix, StringComparison.Ordinal)
            && uint.TryParse(name.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return new ParamDescriptor(name, ParamType.Float, new ParamValue.Float(0), name, "A parameter of the plugin, by its CLAP id; plugin.params names them.", Min: -1e9, Max: 1e9);
        }

        if (owner.Kind == ParamOwnerKind.Effect && EffectCatalog.Registry.Find(owner.Effect!.TypeId) is null)
        {
            throw new CommandException(
                "unknown-effect",
                $"Effect '{owner.Id}' is a '{owner.Effect.TypeId}', which this build does not have, so its parameters cannot be changed.");
        }

        string names = string.Join(", ", ParamTargets.Params(owner, EffectCatalog.Registry).Select(parameter => parameter.Name));
        throw new CommandException(
            "unknown-param",
            names.Length == 0
                ? $"'{name}' is not a parameter of {Describe(owner)}, which has none."
                : $"'{name}' is not a parameter of {Describe(owner)}. It has {names}.");
    }

    /// <summary>A value typed for a parameter, checked against its limits.</summary>
    internal static ParamValue Value(ParamDescriptor descriptor, string text)
    {
        ParamValue value = ParamValues.Parse(descriptor, text);
        ParamValues.Check(descriptor, value);
        return value;
    }

    /// <summary>
    /// A time as the owner's keyframes store it: from the clip's start for anything on a clip,
    /// from the sequence's start for a track. Refused outside the clip, which is almost always a
    /// clip time given without <c>--local</c> or the other way round.
    /// </summary>
    internal static Flicks Local(ParamOwner owner, Flicks at, bool local)
    {
        Flicks time = local ? at : at - owner.Origin;

        if (time.IsNegative || (owner.Clip is not null && time > owner.Length))
        {
            if (local)
            {
                throw new CommandException(
                    "time-out-of-range",
                    $"{Timecode.FormatClock(at)} from the start of {Describe(owner)} is outside it; it lasts {Timecode.FormatClock(owner.Length)}. Without --local, times are on the sequence.");
            }

            string where = owner.Clip is { } clip
                ? $"clip '{clip.Name}', which runs from {Timecode.FormatClock(clip.Start)} to {Timecode.FormatClock(clip.End)} on the sequence. Use --local for a time from the clip's start"
                : "the sequence, which starts at 00:00:00.000";
            throw new CommandException("time-out-of-range", $"{Timecode.FormatClock(at)} is outside {where}.");
        }

        return time;
    }

    /// <summary>Half a frame of the owner's sequence: how near a time has to be to find a keyframe.</summary>
    internal static Flicks Tolerance(Project project, ParamOwner owner) =>
        new(Flicks.FromFrames(1, project.SettingsFor(owner.Sequence).FrameRate).Value / 2);

    /// <summary>The keyframes of a parameter that has some, or a refusal.</summary>
    internal static KeyframedValue Keyframed(ParamOwner owner, ParamDescriptor descriptor) =>
        ParamTargets.Get(owner, descriptor.Name) is KeyframedValue { IsAnimated: true } keyed
            ? keyed
            : throw new CommandException(
                "not-animated",
                $"'{descriptor.Name}' on {Describe(owner)} has no keyframes. Add one with 'jazz keyframe add {owner.Id} {descriptor.Name} --at <time>'.");

    /// <summary>The index of the keyframe nearest a time within the tolerance, or -1.</summary>
    internal static int Nearest(KeyframedValue keyed, Flicks time, Flicks tolerance)
    {
        int best = -1;
        long bestDistance = long.MaxValue;

        for (int index = 0; index < keyed.Keyframes.Length; index++)
        {
            long distance = Math.Abs((keyed.Keyframes[index].Time - time).Value);
            if (distance <= tolerance.Value && distance < bestDistance)
            {
                best = index;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>The keyframe at a time, or a refusal that says where the keyframes are.</summary>
    internal static int Find(ParamOwner owner, ParamDescriptor descriptor, KeyframedValue keyed, Flicks time, Flicks tolerance)
    {
        int index = Nearest(keyed, time, tolerance);
        if (index >= 0)
        {
            return index;
        }

        string times = string.Join(", ", keyed.Keyframes.Select(key => Timecode.FormatClock(key.Time + owner.Origin)));
        throw new CommandException(
            "keyframe-not-found",
            $"'{descriptor.Name}' has no keyframe at {Timecode.FormatClock(time + owner.Origin)} on the sequence. It has keyframes at {times}.");
    }

    /// <summary>
    /// A keyframed value with a keyframe added at a time, or the one already there changed. A
    /// new keyframe takes the shape of the one before it, so adding a point to an eased curve
    /// keeps it eased.
    /// </summary>
    internal static KeyframedValue Upsert(KeyframedValue? keyed, ParamDescriptor descriptor, Flicks time, ParamValue value, Interp? interp, Flicks tolerance)
    {
        Interp discrete = Interp.Hold;
        bool continuous = descriptor.Default.IsContinuous;

        if (keyed is null || !keyed.IsAnimated)
        {
            return new KeyframedValue([new Keyframe(time, value, continuous ? interp ?? Interp.Linear : discrete)]);
        }

        int existing = Nearest(keyed, time, tolerance);
        if (existing >= 0)
        {
            Keyframe current = keyed.Keyframes[existing];
            Keyframe changed = current with { Value = value, Interp = continuous ? interp ?? current.Interp : discrete };
            return new KeyframedValue(keyed.Keyframes.SetItem(existing, changed));
        }

        Keyframe? before = keyed.Keyframes.LastOrDefault(key => key.Time <= time);
        Interp shape = continuous ? interp ?? before?.Interp ?? Interp.Linear : discrete;
        return new KeyframedValue(keyed.Keyframes.Add(new Keyframe(time, value, shape)));
    }

    /// <summary>
    /// Stores a parameter and reports the owner as changed, with the clip or track it sits on,
    /// which is what redraws.
    /// </summary>
    internal static Project Store(Project project, ParamOwner owner, ParamDescriptor descriptor, AnimatedValue? value, HandlerContext context, bool driver = false)
    {
        // A driven parameter's value is the driver's; a set or a keyframe would silently replace it.
        // A reset (no value) puts it back to its default, driver and all.
        if (!driver && value is not null && ParamTargets.Get(owner, descriptor.Name) is DrivenValue)
        {
            throw new CommandException(
                "param-driven",
                $"'{descriptor.Name}' is driven by an expression, so it has no value or keyframes to set. Change the driver with 'jazz param set-driver', or clear it with 'jazz param clear-driver {owner.Id} {descriptor.Name}'.",
                "param");
        }

        Project changed = ParamTargets.Set(project, owner, descriptor, value);
        if (changed == project)
        {
            return project;
        }

        Touched(owner, context);
        return changed;
    }

    /// <summary>Reports an owner and whatever it sits on as changed.</summary>
    internal static void Touched(ParamOwner owner, HandlerContext context)
    {
        context.Changed(owner.Id);
        context.Changed(owner.Clip?.Id ?? owner.Track.Id);
    }

    /// <summary>A short description of an owner for a message.</summary>
    internal static string Describe(ParamOwner owner) => owner.Kind switch
    {
        ParamOwnerKind.Clip => $"clip '{owner.Clip!.Name}'",
        ParamOwnerKind.Track => $"track '{owner.Track.Name}'",
        ParamOwnerKind.Mask => $"a mask on clip '{owner.Clip!.Name}'",
        ParamOwnerKind.Transition => $"transition '{EffectCatalog.Registry.Find(owner.Transition!.TypeId)?.Name ?? owner.Transition.TypeId}'",
        _ => $"effect '{EffectCatalog.Registry.Find(owner.Effect!.TypeId)?.Name ?? owner.Effect.TypeId}'",
    };

    /// <summary>One parameter as a query reports it.</summary>
    internal static ParamInfo Info(ParamOwner owner, string section, ParamDescriptor descriptor, Flicks? at = null)
    {
        AnimatedValue? value = ParamTargets.Get(owner, descriptor.Name);
        Flicks when = at is { } time ? time - owner.Origin : Flicks.Zero;

        KeyframeInfo[] keyframes = value is KeyframedValue keyed
            ? [.. keyed.Keyframes.Select(key => new KeyframeInfo(
                key.Time + owner.Origin,
                key.Time,
                ParamValues.Format(ParamValues.Coerce(descriptor, key.Value) ?? key.Value),
                key.Interp,
                key.InHandle is { } handleIn ? ParamValues.Format(new ParamValue.Float2(handleIn)) : null,
                key.OutHandle is { } handleOut ? ParamValues.Format(new ParamValue.Float2(handleOut)) : null))]
            : [];

        return new ParamInfo(
            owner.Id,
            descriptor.Name,
            section,
            ParamSchemaInfo.From(descriptor),
            value is null,
            value is KeyframedValue { IsAnimated: true },
            ParamValues.Format(ParamEval.Eval(value, descriptor, when)),
            keyframes);
    }

    /// <summary>Every parameter of an owner as a query reports them, section by section.</summary>
    internal static ParamInfo[] Infos(ParamOwner owner, Flicks? at = null) =>
        [.. ParamTargets.Sections(owner, EffectCatalog.Registry)
            .SelectMany(section => section.Params.Select(parameter => Info(owner, section.Name, parameter, at)))];
}
