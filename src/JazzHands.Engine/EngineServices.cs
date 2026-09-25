using System.Reflection;
using JazzHands.Core.Commands;
using JazzHands.Engine.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JazzHands.Engine;

/// <summary>Wires the engine into a service collection.</summary>
/// <remarks>
/// Handlers are found by scanning rather than listed, for the same reason commands are: a handler
/// that exists but was never added to a list is a command that fails at runtime with "nothing
/// handles this", and nobody finds out until someone types it.
/// <see cref="VerifyEveryCommandHasAHandler"/> turns that into a test instead.
/// </remarks>
public static class EngineServices
{
    /// <summary>Registers every command and query handler in the engine assembly.</summary>
    public static IServiceCollection AddJazzHandsEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The media handlers need an importer, and the importer needs somewhere to cache probes.
        // Both are singletons: the cache holds a SQLite connection, and opening one per import
        // would cost more than the probe it saves. Where it lives and how big it may get are the
        // editor's settings; a test registers its own store first to keep away from the real one.
        services.TryAddSingleton(_ => new Caching.CacheSettingsStore());

        // The settings the settings commands reach: the cache section here, the others added by
        // the control server and the editor as they register.
        services.AddSingleton(new Settings.SettingsSectionType("cache", typeof(Caching.CacheSettings), "Where the cache lives and how big it may get (from the next start; cache.configure changes it now)"));
        services.TryAddSingleton(provider => new Settings.SettingsCatalog(
            provider.GetRequiredService<Caching.CacheSettingsStore>().Path,
            provider.GetServices<Settings.SettingsSectionType>()));
        services.TryAddSingleton(provider =>
        {
            Caching.CacheSettings settings = provider.GetRequiredService<Caching.CacheSettingsStore>().Current;
            return new Media.Import.CacheManager(settings.Location) { CapBytes = settings.CapBytes };
        });

        // Thumbnails, waveforms and proxies. The first two start their worker threads when first
        // asked for, which a headless command that never draws a timeline never does.
        services.TryAddSingleton(provider => new Caching.ProxyService(provider.GetRequiredService<Media.Import.CacheManager>()));
        services.TryAddSingleton(provider => new Caching.ThumbnailService(provider.GetRequiredService<Media.Import.CacheManager>()));
        services.TryAddSingleton(provider => new Caching.WaveformService(provider.GetRequiredService<Media.Import.CacheManager>()));

        // What the editor is pointing at. One per host, like the session it belongs to; the
        // selection commands find it here, which is what lets a script select and then act.
        services.TryAddSingleton<Selection.SelectionService>();

        // Folders watched for new recordings, per process; each import goes through its session.
        services.TryAddSingleton(_ => new Library.MediaWatchService());

        // Frames looked at by queries (the eyedropper, the scopes read headless) are drawn on WARP
        // by a device of their own, made on first use.
        services.TryAddSingleton(_ => new Frames.StillRenderer());

        // Keyframe indexes for planning exports, shared so a file is scanned once per process.
        services.TryAddSingleton(provider => new Export.KeyframeLookup(provider.GetService<Media.Import.CacheManager>()));
        services.TryAddSingleton(provider =>
            new Media.Import.MediaImporter(provider.GetService<Media.Import.CacheManager>()));

        foreach (Type type in typeof(EngineServices).Assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsClass)
            {
                continue;
            }

            foreach (Type contract in type.GetInterfaces())
            {
                if (!contract.IsGenericType)
                {
                    continue;
                }

                Type definition = contract.GetGenericTypeDefinition();

                if (definition == typeof(ICommandHandler<>) || definition == typeof(IQueryHandler<,>))
                {
                    services.AddSingleton(contract, type);
                }
            }
        }

        return services;
    }

    /// <summary>
    /// Names every command and query with nothing to handle it.
    /// </summary>
    /// <remarks>
    /// Empty is the only acceptable answer, and a test says so. The dispatcher handles undo, redo
    /// and batch itself, so those three are expected to have no handler.
    /// </remarks>
    public static IReadOnlyList<string> VerifyEveryCommandHasAHandler(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var missing = new List<string>();

        foreach (CommandMetadata metadata in CommandRegistry.All)
        {
            if (IsHandledByTheDispatcher(metadata.Type))
            {
                continue;
            }

            Type handlerType = metadata.IsQuery
                ? typeof(IQueryHandler<,>).MakeGenericType(metadata.Type, metadata.ResultType!)
                : typeof(ICommandHandler<>).MakeGenericType(metadata.Type);

            if (services.GetService(handlerType) is null)
            {
                missing.Add(metadata.Name);
            }
        }

        return missing;
    }

    /// <summary>The three the dispatcher runs itself, because they are about history, not the project.</summary>
    internal static bool IsHandledByTheDispatcher(Type commandType) =>
        commandType == typeof(UndoCommand)
        || commandType == typeof(RedoCommand)
        || commandType == typeof(BatchCommand);
}
