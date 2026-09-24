using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Effects;

namespace JazzHands.Engine.Handlers;

/// <summary>Lists the effect types.</summary>
public sealed class ListEffectsHandler : IQueryHandler<ListEffectsQuery, EffectTypeInfo[]>
{
    /// <inheritdoc />
    public EffectTypeInfo[] Handle(Project project, ListEffectsQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(query);

        return
        [
            .. EffectCatalog.Registry.All
                .Where(descriptor => query.Kind is not { } kind || descriptor.Kind == kind)
                .Where(descriptor => query.Search is not { Length: > 0 } search
                    || descriptor.TypeId.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || descriptor.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || descriptor.Category.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Select(Describe),
        ];
    }

    /// <summary>One type as the query reports it.</summary>
    internal static EffectTypeInfo Describe(EffectDescriptor descriptor) => new(
        descriptor.TypeId,
        descriptor.Kind,
        descriptor.Name,
        descriptor.Category,
        descriptor.Description,
        [.. descriptor.Params.Select(ParamSchemaInfo.From)]);
}

/// <summary>Shows one effect instance.</summary>
public sealed class GetEffectHandler : IQueryHandler<GetEffectQuery, EffectInfo>
{
    /// <inheritdoc />
    public EffectInfo Handle(Project project, GetEffectQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);

        ParamOwner owner = ParamHelp.Effect(project, query.EffectId);
        Effect effect = owner.Effect!;
        EffectDescriptor? descriptor = EffectCatalog.Registry.Find(effect.TypeId);

        return new EffectInfo(
            effect.Id,
            effect.TypeId,
            descriptor?.Name ?? effect.TypeId,
            descriptor is not null,
            effect.Enabled,
            owner.Clip?.Id ?? owner.Track.Id,
            EffectChains.IndexOf(owner.Clip, owner.Clip?.Effects ?? owner.Track.Effects, effect.Id),
            ParamHelp.Infos(owner));
    }
}

/// <summary>Copies effects as JSON.</summary>
public sealed class CopyEffectsHandler : IQueryHandler<CopyEffectsQuery, string>
{
    /// <inheritdoc />
    public string Handle(Project project, CopyEffectsQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);

        return new EffectClipboard(new EquatableArray<Effect>(EffectHelp.Collect(project, query.Ids))).ToJson();
    }
}

/// <summary>Lists the project's effect presets.</summary>
public sealed class ListEffectPresetsHandler : IQueryHandler<ListEffectPresetsQuery, EffectPresetInfo[]>
{
    /// <inheritdoc />
    public EffectPresetInfo[] Handle(Project project, ListEffectPresetsQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);

        return
        [
            .. project.EffectPresets.Select(preset => new EffectPresetInfo(preset.Id, preset.Name, [.. preset.Effects.Select(effect => effect.TypeId)])),
            .. Looks.All
                .Where(look => !project.EffectPresets.Any(preset => string.Equals(preset.Name, look.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(look => new EffectPresetInfo(look.Id, look.Name, [.. look.Effects.Select(effect => effect.TypeId)], BuiltIn: true)),
        ];
    }
}

/// <summary>A preset as JSON.</summary>
public sealed class ExportEffectPresetHandler : IQueryHandler<ExportEffectPresetQuery, string>
{
    /// <inheritdoc />
    public string Handle(Project project, ExportEffectPresetQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);

        return EffectClipboard.PresetToJson(EffectHelp.Preset(project, query.Preset));
    }
}

/// <summary>Lists the parameters of a clip, track, effect or mask.</summary>
public sealed class ListParamsHandler : IQueryHandler<ListParamsQuery, ParamInfo[]>
{
    /// <inheritdoc />
    public ParamInfo[] Handle(Project project, ListParamsQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);

        return ParamHelp.Infos(ParamHelp.Owner(project, query.OwnerId));
    }
}

/// <summary>Shows one parameter.</summary>
public sealed class GetParamHandler : IQueryHandler<GetParamQuery, ParamInfo>
{
    /// <inheritdoc />
    public ParamInfo Handle(Project project, GetParamQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);

        ParamOwner owner = ParamHelp.Owner(project, query.OwnerId);
        ParamDescriptor descriptor = ParamHelp.Param(owner, query.Param);
        string section = ParamTargets.Sections(owner, EffectCatalog.Registry).First(candidate => candidate.Param(descriptor.Name) is not null).Name;
        return ParamHelp.Info(owner, section, descriptor, query.At);
    }
}
