using JazzHands.App.Services;
using JazzHands.App.ViewModels;
using JazzHands.App.ViewModels.Audio;
using JazzHands.App.ViewModels.Media;
using JazzHands.App.ViewModels.Playback;
using JazzHands.Core.Model;
using JazzHands.Engine;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Playback;
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

        services.AddSingleton(provider => new Session(project, provider, path, recovery: path.Length > 0));
        services.AddSingleton<ISession>(provider => new EngineSession(provider.GetRequiredService<Session>()));

        services.AddSingleton<IUiDispatcher, WpfDispatcher>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<IDialogService>(provider =>
            new DialogService(provider.GetRequiredService<ImportViewModel>));

        services.AddTransient<ImportViewModel>();

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
                cacheManager: provider.GetService<Media.Import.CacheManager>());
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
            provider.GetRequiredService<IFullScreenPreview>()));

        services.AddSingleton<MediaPanelViewModel>();
        services.AddSingleton<MetersPanelViewModel>(provider => new MetersPanelViewModel(
            provider.GetRequiredService<IMeterFeed>(),
            provider.GetRequiredService<IUiDispatcher>()));
        services.AddSingleton<MainViewModel>();

        return services;
    }
}
