using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Caching;
using JazzHands.Engine.Commands;
using JazzHands.Media.Import;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace JazzHands.Engine.Handlers;

/// <summary>Finds the cache services for the cache and proxy commands.</summary>
internal static class CacheHelp
{
    /// <summary>The cache, or a coded refusal when this process has none.</summary>
    internal static CacheManager Cache(IServiceProvider? services) =>
        services?.GetService<CacheManager>()
        ?? throw new CommandException("no-cache", "This session has no cache.");

    /// <summary>The proxy service, or a coded refusal.</summary>
    internal static ProxyService Proxies(IServiceProvider? services) =>
        services?.GetService<ProxyService>()
        ?? throw new CommandException("no-cache", "This session has no proxy service.");

    /// <summary>
    /// Forgets everything made from content that is gone: a file replaced in place. The disk cache,
    /// what the thumbnail and waveform services hold in memory, and the proxy.
    /// </summary>
    internal static void ForgetContent(IServiceProvider? services, CacheManager? cache, string hash)
    {
        if (hash.Length == 0)
        {
            return;
        }

        cache?.Forget(hash);
        services?.GetService<ThumbnailService>()?.Forget(hash);
        services?.GetService<WaveformService>()?.Forget(hash);
        services?.GetService<SceneCutService>()?.Forget(hash);

        try
        {
            services?.GetService<ProxyService>()?.Remove(hash);
        }
        catch (CommandException error)
        {
            // Open for playback; it is stale but harmless, and goes with the next clear.
            Log.ForContext(typeof(CacheHelp)).Warning("A stale proxy could not be deleted: {Error}", error.Message);
        }
    }
}

/// <summary>Says what the cache holds.</summary>
public sealed class GetCacheStatsHandler : IQueryHandler<GetCacheStatsQuery, CacheStatsInfo>
{
    /// <inheritdoc />
    public CacheStatsInfo Handle(Project project, GetCacheStatsQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        CacheManager cache = CacheHelp.Cache(context.Services);
        CacheUsage usage = cache.Usage();

        ProxyService? proxies = context.Services?.GetService<ProxyService>();
        proxies?.Refresh();
        IReadOnlyCollection<ProxyFile> files = proxies?.Files ?? [];

        int pending = (context.Services?.GetService<ThumbnailService>()?.Work.Pending ?? 0)
            + (context.Services?.GetService<WaveformService>()?.Work.Pending ?? 0);

        return new CacheStatsInfo(
            cache.Folder,
            cache.CapBytes,
            usage.BlobBytes,
            usage.DatabaseBytes,
            usage.Probes,
            usage.KeyframeIndexes,
            usage.Thumbnails,
            usage.Waveforms,
            files.Count,
            files.Sum(file => file.Bytes),
            cache.Evicted,
            pending,
            proxies?.Enabled ?? false);
    }
}

/// <summary>Empties the cache, or parts of it, on disk and in memory.</summary>
public sealed class ClearCacheHandler : ICommandHandler<ClearCacheCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ClearCacheCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        CacheManager cache = CacheHelp.Cache(context.Services);
        ThumbnailService? thumbnails = context.Services?.GetService<ThumbnailService>();
        WaveformService? waveforms = context.Services?.GetService<WaveformService>();
        SceneCutService? scenes = context.Services?.GetService<SceneCutService>();
        ProxyService? proxies = context.Services?.GetService<ProxyService>();

        CacheParts parts = PartsOf(command);
        bool dropProxies = command.Proxies || command.All;

        if (command.MediaId is { Length: > 0 } id)
        {
            MediaItem item = MediaServices.Require(project, id);
            if (item.Hash.Length > 0)
            {
                cache.Forget(item.Hash, parts);

                if (parts.HasFlag(CacheParts.Thumbnails))
                {
                    thumbnails?.Forget(item.Hash);
                }

                if (parts.HasFlag(CacheParts.Waveforms))
                {
                    waveforms?.Forget(item.Hash);
                }

                if (parts.HasFlag(CacheParts.Analyses))
                {
                    scenes?.Forget(item.Hash);
                }

                if (dropProxies)
                {
                    proxies?.Remove(item.Hash);
                }
            }

            context.Changed(item.Id);
            return project;
        }

        cache.Clear(parts);

        if (parts.HasFlag(CacheParts.Thumbnails))
        {
            thumbnails?.Clear();
        }

        if (parts.HasFlag(CacheParts.Waveforms))
        {
            waveforms?.Clear();
        }

        if (parts.HasFlag(CacheParts.Analyses))
        {
            scenes?.Clear();
        }

        if (dropProxies)
        {
            proxies?.RemoveAll();
        }

        return project;
    }

    /// <summary>
    /// The parts named, or when only proxies or nothing is named, what that means: nothing but
    /// proxies, or everything made again on demand.
    /// </summary>
    private static CacheParts PartsOf(ClearCacheCommand command)
    {
        if (command.All)
        {
            return CacheParts.All;
        }

        CacheParts parts = CacheParts.None;
        parts |= command.Thumbnails ? CacheParts.Thumbnails : CacheParts.None;
        parts |= command.Waveforms ? CacheParts.Waveforms : CacheParts.None;
        parts |= command.Probes ? CacheParts.Probes : CacheParts.None;
        parts |= command.Keyframes ? CacheParts.Keyframes : CacheParts.None;
        parts |= command.Analyses ? CacheParts.Analyses : CacheParts.None;

        return parts == CacheParts.None && !command.Proxies ? CacheParts.All : parts;
    }
}

/// <summary>Saves where the cache lives and how big it may get, and applies the size limit now.</summary>
public sealed class ConfigureCacheHandler : ICommandHandler<ConfigureCacheCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ConfigureCacheCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        CacheSettingsStore store = context.Services?.GetService<CacheSettingsStore>()
            ?? throw new CommandException("no-cache", "This session has no settings to change.");

        if (command.Location is null && command.CapGb is null)
        {
            throw new CommandException("nothing-to-change", "Give --location, --cap-gb or both.");
        }

        CacheSettings settings = store.Current;

        if (command.Location is { } location)
        {
            if (!Path.IsPathFullyQualified(location))
            {
                throw new CommandException("invalid-value", $"'{location}' is not a full path. The cache folder has to be one.");
            }

            settings = settings with { Location = location };
        }

        if (command.CapGb is { } gigabytes)
        {
            if (!double.IsFinite(gigabytes) || gigabytes < 0)
            {
                throw new CommandException("invalid-value", "The size limit is 0 (none) or more gigabytes.");
            }

            settings = settings with { CapBytes = (long)(gigabytes * 1024 * 1024 * 1024) };
        }

        store.Save(settings);

        if (command.CapGb is not null && context.Services?.GetService<CacheManager>() is { } cache)
        {
            cache.CapBytes = settings.CapBytes;
        }

        return project;
    }
}
