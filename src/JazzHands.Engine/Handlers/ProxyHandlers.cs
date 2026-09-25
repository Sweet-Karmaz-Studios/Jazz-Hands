using JazzHands.Core.Commands;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Engine.Caching;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Export;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Engine.Handlers;

/// <summary>Makes proxies: on the export queue in the editor, before returning in a headless session.</summary>
/// <remarks>
/// A proxy that is already there at the size and in the format asked for is not made again. The
/// ids reported changed are the export jobs' when queued, as <c>export.enqueue</c> does, and the
/// media items' when made here.
/// </remarks>
public sealed class GenerateProxyHandler : ICommandHandler<GenerateProxyCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, GenerateProxyCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ProxyService proxies = CacheHelp.Proxies(context.Services);
        ProxyPresetInfo preset = ProxyPresets.Find(command.Preset)
            ?? throw new CommandException(
                "unknown-preset",
                $"There is no proxy preset called '{command.Preset}'. They are {string.Join(", ", ProxyPresets.All.Select(p => p.Name))}.");

        if (!double.IsFinite(command.Scale) || command.Scale <= 0 || command.Scale > 1)
        {
            throw new CommandException("invalid-value", "A proxy's scale is more than 0 and at most 1: 0.5 for half size, 0.25 for a quarter.");
        }

        IExportService? queue = context.Services?.GetService<IExportService>();
        ExportEnvironment? environment = context.Services?.GetService<ExportEnvironment>();

        foreach (MediaItem item in Targets(project, command))
        {
            string path = HandlerHelp.Resolve(context, item.RelativePath);
            if (!File.Exists(path))
            {
                throw new CommandException("media-missing", $"'{item.Name}' is not at {path}, so it cannot have a proxy made. Relink it first.");
            }

            if (proxies.Find(item.Hash) is { } existing
                && string.Equals(existing.Path, Path.GetFullPath(proxies.PathFor(item.Hash, command.Scale, preset)), StringComparison.OrdinalIgnoreCase))
            {
                context.Changed(item.Id);
                continue;
            }

            context.Cancellation.ThrowIfCancellationRequested();
            string? job = proxies.Generate(item, path, preset, command.Scale, queue, environment, context.Cancellation);
            context.Changed(job ?? item.Id);
        }

        return project;
    }

    private static IEnumerable<MediaItem> Targets(Project project, GenerateProxyCommand command)
    {
        if (command.MediaId is { Length: > 0 } id)
        {
            return [MediaServices.Require(project, id)];
        }

        if (!command.All && !command.Auto)
        {
            throw new CommandException("nothing-chosen", "Name a media item with --media, or pass --all or --auto.");
        }

        IEnumerable<MediaItem> movies = project.Media.Where(item =>
            item.Kind == MediaKind.Movie && item.Info?.VideoStreams.Any() == true && item.Hash.Length > 0);

        return command.All ? movies : movies.Where(ProxyPresets.IsWorthAProxy);
    }
}

/// <summary>Deletes proxies.</summary>
public sealed class RemoveProxyHandler : ICommandHandler<RemoveProxyCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveProxyCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ProxyService proxies = CacheHelp.Proxies(context.Services);

        IEnumerable<MediaItem> items = command.MediaId is { Length: > 0 } id
            ? [MediaServices.Require(project, id)]
            : command.All
                ? project.Media
                : throw new CommandException("nothing-chosen", "Name a media item with --media, or pass --all.");

        foreach (MediaItem item in items)
        {
            if (item.Hash.Length > 0 && proxies.Remove(item.Hash))
            {
                context.Changed(item.Id);
            }
        }

        return project;
    }
}

/// <summary>Switches proxy playback on or off.</summary>
public sealed class SetProxiesEnabledHandler : ICommandHandler<SetProxiesEnabledCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetProxiesEnabledCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        CacheHelp.Proxies(context.Services).Enabled = command.Enabled;
        return project;
    }
}

/// <summary>Lists each movie's proxy.</summary>
public sealed class ListProxiesHandler : IQueryHandler<ListProxiesQuery, ProxyInfo[]>
{
    /// <inheritdoc />
    public ProxyInfo[] Handle(Project project, ListProxiesQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);

        ProxyService proxies = CacheHelp.Proxies(context.Services);
        proxies.Refresh();
        return proxies.List(project, context.Services?.GetService<IExportService>());
    }
}
