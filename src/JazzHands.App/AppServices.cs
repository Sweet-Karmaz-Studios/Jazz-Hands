using JazzHands.App.Services;
using JazzHands.App.ViewModels;
using JazzHands.App.ViewModels.Audio;
using JazzHands.App.ViewModels.Export;
using JazzHands.App.ViewModels.Media;
using JazzHands.App.ViewModels.Playback;
using JazzHands.App.ViewModels.Timeline;
using JazzHands.Core.Model;
using JazzHands.Engine;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Export;
using JazzHands.Engine.Playback;
using JazzHands.Engine.Selection;
using JazzHands.Render;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.App;

/// <summary>Wires the application's services, panels and dialogs.</summary>
/// <remarks>
/// Panels are singletons: one media panel exists for the life of the window, and the docking
/// layout refers to it by content id. Dialogs are transient, because each one is about a
/// different set of files.
/// </remarks>
public static class AppServices
{
    /// <summary>Registers everything the window needs.</summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="project">The project to open.</param>
    /// <param name="path">Where it lives, or empty for one that has never been saved.</param>
    public static IServiceCollection AddJazzHandsApp(this IServiceCollection services, Project project, string path)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(project);

        services.AddJazzHandsEngine();

        // The selection prunes itself after every command, so a deleted clip never stays selected.
        services.AddSingleton(provider =>
        {
            var session = new Session(project, provider, path, recovery: path.Length > 0);
            provider.GetRequiredService<SelectionService>().Attach(session);
            return session;
        });
        services.AddSingleton<ISession>(provider => new EngineSession(provider.GetRequiredService<Session>()));

        services.AddSingleton<IUiDispatcher, WpfDispatcher>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<IDialogService>(provider =>
            new DialogService(provider.GetRequiredService<ImportViewModel>, provider.GetRequiredService<ExportDialogViewModel>));

        services.AddTransient<ImportViewModel>();
        services.AddTransient<ExportDialogViewModel>();

        // The export queue: jobs from the dialog, the CLI with --attach and MCP alike, kept in
        // %LOCALAPPDATA%JazzHandsqueue.db so they outlive the window. It renders on a device of
        // its own, so an export never queues work in front of a preview present.
        services.AddSingleton(_ =>
        {
            var queue = new ExportQueue();
            queue.Start();
            return queue;
        });
        services.AddSingleton<IExportService>(provider => provider.GetRequiredService<ExportQueue>());
        services.AddSingleton(provider => new ExportQueuePanelViewModel(
            provider.GetRequiredService<ISession>(),
            provider.GetRequiredService<IExportService>(),
            provider.GetRequiredService<IUiDispatcher>()));

        // The transport follows the session: every command rebuilds the mix it plays. It opens
        // the default sound card, or a silent clock on a machine with none, and plays nothing
        // until something asks it to. Its format is the project's at startup.
        services.AddSingleton(provider =>
        {
            ProjectSettings settings = project.ActiveSequence is { } sequence ? project.SettingsFor(sequence) : project.Settings;
            Transport transport = Transport.ForDefaultDevice(settings.SampleRate, settings.ChannelCount);
            transport.Attach(provider.GetRequiredService<Session>());
            return transport;
        });
        services.AddSingleton<IMeterFeed>(provider => new TransportMeterFeed(provider.GetRequiredService<Transport>()));

        // One device for decode, the preview and everything else that touches the GPU. WARP when
        // there is no hardware adapter, so the editor still opens on a machine without one.
        services.AddSingleton(_ => RenderDevice.Create());

        // The playback engine is what the playback.* commands drive: the session finds it among
        // these services as IPlaybackController, which is what makes a command from the CLI or
        // MCP move the same playhead as the space bar.
        services.AddSingleton(provider =>
        {
            Session session = provider.GetRequiredService<Session>();
            var engine = new PlaybackEngine(
                provider.GetRequiredService<Transport>(),
                provider.GetRequiredService<RenderDevice>(),
                notices: session.Notices,
                cacheManager: provider.GetService<Media.Import.CacheManager>(),
                proxies: provider.GetService<Engine.Caching.ProxyService>());
            engine.Attach(session);
            return engine;
        });
        services.AddSingleton<IPlaybackController>(provider => provider.GetRequiredService<PlaybackEngine>());
        services.AddSingleton<IPreviewEngine>(provider => new EnginePreview(
            provider.GetRequiredService<PlaybackEngine>(),
            provider.GetRequiredService<RenderDevice>()));
        services.AddSingleton<IFullScreenPreview>(provider => new FullScreenPreview(
            provider.GetRequiredService<IPreviewEngine>(),
            provider.GetRequiredService<PreviewPanelViewModel>));
        services.AddSingleton(provider => new PreviewPanelViewModel(
            provider.GetRequiredService<ISession>(),
            provider.GetRequiredService<IPreviewEngine>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<IFullScreenPreview>(),
            provider.GetRequiredService<PointPicker>()));

        // Thumbnails and waveforms: the engine's caches, turned into bitmaps and peaks for the
        // timeline and the media panel, with their ready events folded onto the UI thread.
        services.AddSingleton(provider => new CachedThumbnails(
            provider.GetRequiredService<ISession>(),
            provider.GetRequiredService<Engine.Caching.ThumbnailService>(),
            provider.GetRequiredService<Engine.Caching.WaveformService>(),
            provider.GetRequiredService<IUiDispatcher>()));
        services.AddSingleton<IMediaImagery>(provider => provider.GetRequiredService<CachedThumbnails>());
        services.AddSingleton<Controls.Timeline.ITimelineImagery>(provider => new TimelineImagery(
            provider.GetRequiredService<ISession>(),
            provider.GetRequiredService<CachedThumbnails>(),
            provider.GetRequiredService<IUiDispatcher>()));

        services.AddSingleton(provider => new TimelineDocuments(
            provider.GetRequiredService<ISession>(),
            provider.GetRequiredService<SelectionService>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<IPreviewEngine>(),
            provider.GetRequiredService<IDialogService>(),
            new WindowsClipboardService(),
            provider.GetRequiredService<Controls.Timeline.ITimelineImagery>()));

        // The keymap: embedded defaults under %APPDATA%\JazzHands\keymap.json if there is one.
        services.AddSingleton(provider =>
        {
            Input.Keymap keymap = Input.Keymap.Load(null, out IReadOnlyList<string> problems);
            foreach (string problem in problems)
            {
                Serilog.Log.Warning("Keymap: {Problem}", problem);
            }

            IPreviewEngine preview = provider.GetRequiredService<IPreviewEngine>();
            return new Input.KeymapService(
                provider.GetRequiredService<ISession>(),
                provider.GetRequiredService<SelectionService>(),
                () => preview.Position,
                keymap);
        });

        // The inspector and the effects browser. The point picker is what lets the inspector pick
        // a point on the preview; the favorites are the person's, kept beside the keymap.
        services.AddSingleton<PointPicker>();
        services.AddSingleton<IEffectFavorites>(_ => new FileEffectFavorites());
        services.AddSingleton(provider => new ViewModels.Inspector.InspectorPanelViewModel(
            provider.GetRequiredService<ISession>(),
            provider.GetRequiredService<SelectionService>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<IPreviewEngine>(),
            provider.GetRequiredService<PointPicker>()));
        services.AddSingleton(provider =>
        {
            var previews = new EffectPreviewImages(provider.GetRequiredService<IUiDispatcher>());
            previews.Start();
            return previews;
        });
        services.AddSingleton<IEffectPreviewImages>(provider => provider.GetRequiredService<EffectPreviewImages>());
        services.AddSingleton(provider => new ViewModels.Effects.EffectsPanelViewModel(
            provider.GetRequiredService<ISession>(),
            provider.GetRequiredService<SelectionService>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<IEffectFavorites>(),
            provider.GetRequiredService<IEffectPreviewImages>(),
            provider.GetRequiredService<IPreviewEngine>()));

        services.AddSingleton<MediaPanelViewModel>();
        services.AddSingleton<MetersPanelViewModel>(provider => new MetersPanelViewModel(
            provider.GetRequiredService<IMeterFeed>(),
            provider.GetRequiredService<IUiDispatcher>()));
        services.AddSingleton<MainViewModel>();

        return services;
    }
}
