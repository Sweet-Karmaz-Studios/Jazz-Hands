using System.Globalization;
using JazzHands.Audio.Effects;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Plugins;
using JazzHands.Plugins;
using JazzHands.Plugins.Clap;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Engine.Handlers;

/// <summary>What the plugin commands share (Phase 46).</summary>
internal static class PluginHelp
{
    /// <summary>The catalog, from the session or made for this one command.</summary>
    internal static PluginCatalog Catalog(IServiceProvider? services) => services?.GetService<PluginCatalog>() ?? new PluginCatalog();

    /// <summary>A plugin effect, or a coded refusal.</summary>
    internal static (ParamOwner Owner, Effect Effect) Effect(Project project, string effectId)
    {
        ParamOwner owner = ParamHelp.Effect(project, effectId);
        return owner.Effect is { TypeId: PluginEffect.TypeId } effect
            ? (owner, effect)
            : throw new CommandException("not-a-plugin", $"Effect '{effectId}' is a '{owner.Effect!.TypeId}', not a plugin.");
    }

    /// <summary>A text parameter of an effect, or empty.</summary>
    internal static string Text(Effect effect, string name) =>
        effect.Parameter(name) is StaticValue { Value: ParamValue.Text text } ? text.Value : string.Empty;

    /// <summary>The plugin's own parameter values stored on an effect, by CLAP id (the constant ones).</summary>
    internal static List<(uint Id, double Value)> Values(Effect effect) =>
        [.. effect.Parameters
            .Where(parameter => parameter.Name.StartsWith(PluginEffect.ParameterPrefix, StringComparison.Ordinal))
            .Select(parameter => (Ok: uint.TryParse(parameter.Name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out uint id), Id: id, parameter.Value))
            .Where(entry => entry.Ok && entry.Value is StaticValue { Value: ParamValue.Float })
            .Select(entry => (entry.Id, (double)((ParamValue.Float)((StaticValue)entry.Value).Value).Value))];

    /// <summary>
    /// The effect's plugin in a process of its own, as the project has it: its saved state given
    /// back, then its stored parameter values. For questions and captures without a mix running.
    /// </summary>
    internal static PluginProcess Start(Effect effect)
    {
        string library = Text(effect, "library");
        string id = Text(effect, "plugin");
        if (!File.Exists(library))
        {
            throw new CommandException("plugin-missing", $"The plugin '{id}' is not installed here (its file was {library}). plugin.scan looks again.");
        }

        PluginProcess process;
        try
        {
            process = PluginProcess.Start(library, id);
        }
        catch (Exception exception) when (exception is PluginCrashedException or InvalidOperationException or InvalidDataException or IOException)
        {
            throw new CommandException("plugin-failed", $"The plugin '{id}' could not start: {exception.Message}");
        }

        try
        {
            if (Text(effect, "state") is { Length: > 0 } state)
            {
                process.LoadState(Convert.FromBase64String(state));
            }

            process.Set(Values(effect));
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}

/// <summary>Scans for plugins.</summary>
public sealed class ScanPluginsHandler : ICommandHandler<ScanPluginsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ScanPluginsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        if (command.Folder is { Length: > 0 } folder && !Directory.Exists(folder))
        {
            throw new CommandException("folder-not-found", $"There is no folder {folder}.", "folder");
        }

        PluginHelp.Catalog(context.Services).Scan(command.Folder is { Length: > 0 } extra ? [extra] : null, command.Again, context.Cancellation);
        return project;
    }
}

/// <summary>Answers <c>plugin.list</c>.</summary>
public sealed class ListPluginsHandler : IQueryHandler<ListPluginsQuery, PluginsInfo>
{
    /// <inheritdoc />
    public PluginsInfo Handle(Project project, ListPluginsQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        PluginCatalog catalog = PluginHelp.Catalog(context.Services);
        return new PluginsInfo(
            [.. catalog.Plugins.Select(plugin => new PluginInfo(plugin.Id, plugin.Name, plugin.Vendor, plugin.Version, plugin.Library, [.. plugin.Features]))],
            [.. catalog.Crashed],
            [.. PluginCatalog.StandardFolders]);
    }
}

/// <summary>Puts a plugin on a clip or track.</summary>
public sealed class AddPluginHandler : ICommandHandler<AddPluginCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddPluginCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ParamOwner owner = EffectHelp.ChainOwner(project, command.OwnerId);
        EffectHelp.Suited(owner, PluginEffect.TypeId);
        InstalledPlugin plugin = PluginHelp.Catalog(context.Services).Find(command.PluginId)
            ?? throw new CommandException("plugin-not-found", $"No plugin '{command.PluginId}' has been found here. plugin.scan looks; plugin.list names what was found.");
        string id = HandlerHelp.IdOr(command.EffectId);
        HandlerHelp.RequireUnused(project, id);
        EquatableArray<Effect> chain = EffectHelp.Chain(owner);
        EffectHelp.RequireIndex(command.Index, EffectChains.Visible(owner.Clip, chain).Count, insert: true);

        var effect = new Effect(id, PluginEffect.TypeId, Enabled: true, EquatableArray.Create(
            new EffectParameter("plugin", AnimatedValue.Constant(new ParamValue.Text(plugin.Id))),
            new EffectParameter("library", AnimatedValue.Constant(new ParamValue.Text(plugin.Library)))));

        // A plugin's latency is made up wherever it goes: a clip reads its file ahead, a track
        // mixes its clips ahead (AudioGraph).
        context.Changed(id);
        context.Changed(owner.Id);
        return EffectHelp.WithChain(project, owner, EffectChains.Insert(owner.Clip, chain, command.Index, [effect]));
    }
}

/// <summary>Answers <c>plugin.params</c>.</summary>
public sealed class PluginParamsHandler : IQueryHandler<PluginParamsQuery, PluginParamInfo[]>
{
    /// <inheritdoc />
    public PluginParamInfo[] Handle(Project project, PluginParamsQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);

        (_, Effect effect) = PluginHelp.Effect(project, query.EffectId);
        using PluginProcess process = PluginHelp.Start(effect);
        IReadOnlyDictionary<uint, double> values = process.Values();
        return [.. process.Parameters
            .Where(parameter => parameter.IsEditable)
            .Select(parameter => new PluginParamInfo(
                PluginEffect.ParameterPrefix + parameter.Id.ToString(CultureInfo.InvariantCulture),
                parameter.Id,
                parameter.Name,
                parameter.Min,
                parameter.Max,
                parameter.Default,
                values.TryGetValue(parameter.Id, out double value) ? value : parameter.Default))];
    }
}

/// <summary>Keeps a plugin's state in the project.</summary>
public sealed class SavePluginStateHandler : ICommandHandler<SavePluginStateCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SavePluginStateCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (_, Effect effect) = PluginHelp.Effect(project, command.EffectId);
        byte[]? state = PluginEffect.Find(effect.Id)?.CaptureState();
        if (state is null)
        {
            using PluginProcess process = PluginHelp.Start(effect);
            state = process.SaveState();
        }

        string encoded = Convert.ToBase64String(state);
        return encoded == PluginHelp.Text(effect, "state") ? project : context.Run(project, new SetParamCommand(effect.Id, "state", encoded));
    }
}
