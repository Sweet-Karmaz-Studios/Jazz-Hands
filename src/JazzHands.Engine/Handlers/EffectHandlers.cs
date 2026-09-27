using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Effects;

namespace JazzHands.Engine.Handlers;

/// <summary>What the effect handlers share: owners of chains, and storing a chain back.</summary>
internal static class EffectHelp
{
    /// <summary>A clip or track that can carry effects, unlocked, or a refusal.</summary>
    internal static ParamOwner ChainOwner(Project project, string ownerId)
    {
        ParamOwner owner = ParamHelp.Owner(project, ownerId);
        if (owner.Kind is not (ParamOwnerKind.Clip or ParamOwnerKind.Track))
        {
            throw new CommandException(
                "not-an-effect-owner",
                $"'{ownerId}' is {ParamHelp.Describe(owner)}. Effects go on clips and tracks.");
        }

        if (owner.Track.Kind == TrackKind.Subtitle)
        {
            throw new CommandException("not-an-effect-owner", $"Subtitle track '{owner.Track.Name}' does not take effects.");
        }

        HandlerHelp.RequireUnlocked(owner.Track);
        return owner;
    }

    /// <summary>An effect in a clip's or a track's chain, not a node of a colour graph, or a refusal.</summary>
    internal static ParamOwner InChain(Project project, string effectId, string instead)
    {
        ParamOwner owner = ParamHelp.Effect(project, effectId);
        return owner.Graph is { } graph
            ? throw new CommandException("graph-node", $"'{effectId}' is a node of colour graph '{graph.Id}', not an effect in a chain; use 'jazz {instead}' for it.")
            : owner;
    }

    /// <summary>The effects of a clip or a track.</summary>
    internal static EquatableArray<Effect> Chain(ParamOwner owner) => owner.Clip?.Effects ?? owner.Track.Effects;

    /// <summary>A project with the chain of the clip or track an owner is, or sits on, replaced.</summary>
    internal static Project WithChain(Project project, ParamOwner owner, EquatableArray<Effect> chain) =>
        owner.Clip is { } clip
            ? project.ReplaceTrack(owner.Track.ReplaceClip(clip with { Effects = chain }))
            : project.ReplaceTrack(owner.Track with { Effects = chain });

    /// <summary>Refuses an effect that does not suit what it is going on, by type and by kind.</summary>
    internal static EffectDescriptor Suited(ParamOwner owner, string typeId)
    {
        EffectDescriptor descriptor = EffectCatalog.Registry.Require(typeId);

        if (descriptor.Kind == EffectKind.Generator)
        {
            throw new CommandException(
                "not-an-effect",
                $"'{typeId}' is a generator: it makes a picture rather than changing one. Add it as a clip, with 'jazz clip add --generator {typeId}'.");
        }

        if (descriptor.Kind is EffectKind.Transition or EffectKind.AudioTransition)
        {
            throw new CommandException(
                "not-an-effect",
                $"'{typeId}' is a transition: it goes on the cut between two clips. Add it with 'jazz transition add <outgoing clip> <incoming clip> --type {typeId}'.");
        }

        bool picture = owner.Track.Kind is TrackKind.Video or TrackKind.Adjustment;
        if ((descriptor.Kind == EffectKind.Video) != picture)
        {
            string wants = descriptor.Kind == EffectKind.Video ? "a picture" : "sound";
            string has = picture ? "a picture" : "sound";
            throw new CommandException(
                "wrong-effect-kind",
                $"'{descriptor.Name}' works on {wants}, and {ParamHelp.Describe(owner)} carries {has}.");
        }

        if (descriptor.Implementation is { } type)
        {
            if (typeof(JazzHands.Audio.Effects.IClipEffect).IsAssignableFrom(type) && owner.Clip is null)
            {
                throw new CommandException("clip-only", $"'{descriptor.Name}' goes on a clip, not a track: it reads ahead in the clip's file to stay in time.");
            }

            if (typeof(JazzHands.Audio.Effects.ITrackEffect).IsAssignableFrom(type) && owner.Clip is not null)
            {
                throw new CommandException("track-only", $"'{descriptor.Name}' goes on a track, not a clip: it listens to another track.");
            }
        }

        return descriptor;
    }

    /// <summary>Checks an index into the visible chain; the end is allowed for an insert.</summary>
    internal static void RequireIndex(int? index, int count, bool insert)
    {
        int last = insert ? count : count - 1;
        if (index is { } at && (at < 0 || at > last))
        {
            throw new CommandException(
                "index-out-of-range",
                last < 0 ? "There are no effects to move." : $"The chain runs from 0 to {last}; {at} is outside it.");
        }
    }

    /// <summary>
    /// The effects a list of ids names: an effect is itself, a clip or track is its whole chain.
    /// </summary>
    internal static List<Effect> Collect(Project project, EquatableArray<string> ids)
    {
        if (ids.IsEmpty)
        {
            throw new CommandException("missing-value", "Name at least one effect, clip or track.");
        }

        var effects = new List<Effect>();
        foreach (string id in ids)
        {
            ParamOwner owner = ParamHelp.Owner(project, id);
            switch (owner.Kind)
            {
                case ParamOwnerKind.Effect:
                    effects.Add(owner.Effect!);
                    break;
                case ParamOwnerKind.Clip or ParamOwnerKind.Track:
                    effects.AddRange(EffectChains.Visible(owner.Clip, Chain(owner)));
                    break;
                default:
                    throw new CommandException("not-an-effect-owner", $"'{id}' is {ParamHelp.Describe(owner)}, which has no effects.");
            }
        }

        if (effects.Count == 0)
        {
            throw new CommandException("no-effects", "There are no effects there to copy.");
        }

        return effects;
    }

    /// <summary>Adds effects to a clip or track with new ids, each checked against what it is going on.</summary>
    internal static Project Insert(Project project, ParamOwner owner, IEnumerable<Effect> effects, int? index, HandlerContext context)
    {
        List<Effect> added = [.. EffectChains.WithNewIds(effects)];
        foreach (Effect effect in added)
        {
            Suited(owner, effect.TypeId);
        }

        EquatableArray<Effect> chain = Chain(owner);
        RequireIndex(index, EffectChains.Visible(owner.Clip, chain).Count, insert: true);

        foreach (Effect effect in added)
        {
            context.Changed(effect.Id);
        }

        context.Changed(owner.Id);
        return WithChain(project, owner, EffectChains.Insert(owner.Clip, chain, index, added));
    }

    /// <summary>A preset by id or by name, or a refusal.</summary>
    internal static EffectPreset Preset(Project project, string idOrName) =>
        project.EffectPresets.FirstOrDefault(preset => string.Equals(preset.Id, idOrName, StringComparison.Ordinal))
        ?? project.EffectPresets.FirstOrDefault(preset => string.Equals(preset.Name, idOrName, StringComparison.OrdinalIgnoreCase))
        ?? Looks.Find(idOrName)
        ?? throw new CommandException(
            "preset-not-found",
            $"There is no effect preset '{idOrName}'. There are {string.Join(", ", project.EffectPresets.Concat(Looks.All).Select(preset => preset.Name))}.",
            "/effectPresets");

    /// <summary>Refuses a preset name that is empty or already taken.</summary>
    internal static string PresetName(Project project, string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new CommandException("missing-value", "A preset needs a name: --name.");
        }

        if (project.EffectPresets.Any(preset => string.Equals(preset.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CommandException("duplicate-name", $"There is already a preset called '{trimmed}'. Pick another name, or remove that one first.");
        }

        return trimmed;
    }
}

/// <summary>Adds an effect to a clip or a track.</summary>
public sealed class AddEffectHandler : ICommandHandler<AddEffectCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddEffectCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ParamOwner owner = EffectHelp.ChainOwner(project, command.OwnerId);
        EffectDescriptor descriptor = EffectHelp.Suited(owner, command.TypeId);
        string id = HandlerHelp.IdOr(command.EffectId);
        HandlerHelp.RequireUnused(project, id);

        EquatableArray<Effect> chain = EffectHelp.Chain(owner);
        EffectHelp.RequireIndex(command.Index, EffectChains.Visible(owner.Clip, chain).Count, insert: true);

        // Removing a background needs the clip's file matted first (Phase 43), whichever surface asks.
        if (string.Equals(descriptor.TypeId, Render.Effects.Keying.PersonMatteEffect.TypeId, StringComparison.Ordinal) && owner.Clip is { IsMedia: true } clip)
        {
            MatteHelp.Ensure(project, clip, context);
        }

        // So does enhancing speech: the file's sound goes through the network first.
        if (string.Equals(descriptor.TypeId, JazzHands.Audio.Effects.EnhanceSpeechEffect.TypeId, StringComparison.Ordinal) && owner.Clip is { IsMedia: true } && project.FindClip(owner.Clip.Id) is { } location)
        {
            EnhanceHelp.Ensure(project, location, context);
        }

        var effect = new Effect(id, descriptor.TypeId, Enabled: true, EquatableArray<EffectParameter>.Empty);
        context.Changed(id);
        context.Changed(owner.Id);
        return EffectHelp.WithChain(project, owner, EffectChains.Insert(owner.Clip, chain, command.Index, [effect]));
    }
}

/// <summary>Removes an effect.</summary>
public sealed class RemoveEffectHandler : ICommandHandler<RemoveEffectCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveEffectCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ParamOwner owner = EffectHelp.InChain(project, command.EffectId, "color node-remove");
        HandlerHelp.RequireUnlocked(owner.Track);

        if (EffectChains.IsOwnParameters(owner.Clip, owner.Effect!))
        {
            throw new CommandException(
                "generator-parameters",
                $"That is the parameters of generator clip '{owner.Clip!.Name}', not an effect on it. Remove the clip instead.");
        }

        EquatableArray<Effect> chain = EffectHelp.Chain(owner);
        ParamHelp.Touched(owner, context);
        return EffectHelp.WithChain(project, owner, new EquatableArray<Effect>(chain.Where(effect => effect.Id != command.EffectId)));
    }
}

/// <summary>Moves an effect in its chain.</summary>
public sealed class MoveEffectHandler : ICommandHandler<MoveEffectCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, MoveEffectCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ParamOwner owner = EffectHelp.InChain(project, command.EffectId, "color node-connect");
        HandlerHelp.RequireUnlocked(owner.Track);

        EquatableArray<Effect> chain = EffectHelp.Chain(owner);
        int count = EffectChains.Visible(owner.Clip, chain).Count;
        EffectHelp.RequireIndex(command.Index, count, insert: false);

        if (EffectChains.IndexOf(owner.Clip, chain, command.EffectId) == command.Index)
        {
            return project;
        }

        ParamHelp.Touched(owner, context);
        return EffectHelp.WithChain(project, owner, EffectChains.Move(owner.Clip, chain, command.EffectId, command.Index));
    }
}

/// <summary>Bypasses an effect or turns it back on.</summary>
public sealed class SetEffectEnabledHandler : ICommandHandler<SetEffectEnabledCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetEffectEnabledCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ParamOwner owner = ParamHelp.Effect(project, command.EffectId);
        HandlerHelp.RequireUnlocked(owner.Track);

        if (owner.Effect!.Enabled == command.Enabled)
        {
            return project;
        }

        ParamHelp.Touched(owner, context);
        return ParamTargets.ReplaceEffect(project, owner, owner.Effect with { Enabled = command.Enabled });
    }
}

/// <summary>Sets a parameter of an effect.</summary>
public sealed class SetEffectParamHandler : ICommandHandler<SetEffectParamCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetEffectParamCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ParamOwner owner = ParamHelp.Effect(project, command.EffectId);
        HandlerHelp.RequireUnlocked(owner.Track);
        return SetParamHandler.Set(project, owner, command.Param, command.Value, command.At, command.Local, context);
    }
}

/// <summary>Puts an effect's parameters back to their defaults.</summary>
public sealed class ResetEffectHandler : ICommandHandler<ResetEffectCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ResetEffectCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ParamOwner owner = ParamHelp.Effect(project, command.EffectId);
        HandlerHelp.RequireUnlocked(owner.Track);

        if (command.Param is { } name)
        {
            ParamDescriptor descriptor = ParamHelp.Param(owner, name);
            return ParamHelp.Store(project, owner, descriptor, value: null, context);
        }

        if (owner.Effect!.Parameters.IsEmpty)
        {
            return project;
        }

        ParamHelp.Touched(owner, context);
        return ParamTargets.ReplaceEffect(project, owner, owner.Effect with { Parameters = EquatableArray<EffectParameter>.Empty });
    }
}

/// <summary>Pastes copied effects.</summary>
public sealed class PasteEffectsHandler : ICommandHandler<PasteEffectsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, PasteEffectsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ParamOwner owner = EffectHelp.ChainOwner(project, command.OwnerId);
        EffectClipboard content = EffectClipboard.FromJson(command.Data)
            ?? throw new CommandException("invalid-data", "That is not what effect.copy returned: there are no effects in it.");

        return EffectHelp.Insert(project, owner, content.Effects, command.Index, context);
    }
}

/// <summary>Saves effects as a preset in the project.</summary>
public sealed class SaveEffectPresetHandler : ICommandHandler<SaveEffectPresetCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SaveEffectPresetCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        List<Effect> effects = EffectHelp.Collect(project, command.Ids);
        string name = EffectHelp.PresetName(project, command.Name);
        string id = HandlerHelp.IdOr(command.PresetId);
        HandlerHelp.RequireUnused(project, id);

        // Fresh ids, so a preset never shares one with the effect it was saved from.
        var preset = new EffectPreset(id, name, new EquatableArray<Effect>(EffectChains.WithNewIds(effects)));
        context.Changed(id);
        return project with { EffectPresets = project.EffectPresets.Add(preset) };
    }
}

/// <summary>Applies a preset to a clip or a track.</summary>
public sealed class ApplyEffectPresetHandler : ICommandHandler<ApplyEffectPresetCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ApplyEffectPresetCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ParamOwner owner = EffectHelp.ChainOwner(project, command.OwnerId);
        EffectPreset preset = EffectHelp.Preset(project, command.Preset);
        return EffectHelp.Insert(project, owner, preset.Effects, command.Index, context);
    }
}

/// <summary>Deletes a preset.</summary>
public sealed class RemoveEffectPresetHandler : ICommandHandler<RemoveEffectPresetCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveEffectPresetCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        EffectPreset preset = EffectHelp.Preset(project, command.Preset);
        if (!project.EffectPresets.Any(item => item.Id == preset.Id))
        {
            throw new CommandException("built-in-preset", $"'{preset.Name}' is one of the looks the editor comes with, which cannot be removed.");
        }

        context.Changed(preset.Id);
        return project with { EffectPresets = new EquatableArray<EffectPreset>(project.EffectPresets.Where(item => item.Id != preset.Id)) };
    }
}

/// <summary>Adds a preset from JSON.</summary>
public sealed class ImportEffectPresetHandler : ICommandHandler<ImportEffectPresetCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ImportEffectPresetCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        EffectPreset read = EffectClipboard.PresetFromJson(command.Data)
            ?? throw new CommandException("invalid-data", "That is not an effect preset: it needs a name and at least one effect.");

        foreach (Effect effect in read.Effects)
        {
            EffectCatalog.Registry.Require(effect.TypeId);
        }

        string name = EffectHelp.PresetName(project, command.Name ?? read.Name);
        var preset = new EffectPreset(Id.New(), name, new EquatableArray<Effect>(EffectChains.WithNewIds(read.Effects)));
        context.Changed(preset.Id);
        return project with { EffectPresets = project.EffectPresets.Add(preset) };
    }
}
