using JazzHands.App.Services;
using JazzHands.App.ViewModels;
using JazzHands.App.ViewModels.Audio;
using JazzHands.App.ViewModels.Export;
using JazzHands.App.ViewModels.Media;
using JazzHands.App.ViewModels.Playback;
using JazzHands.App.ViewModels.Timeline;
using JazzHands.Control;
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
    /// <param name="device">
    /// The render device already being created, started early so the GPU comes up while WPF and the
    /// rest of the services do; null creates it when first asked for.
    /// </param>
    public static IServiceCollection AddJazzHandsApp(this IServiceCollection services, Project project, string path, Task<RenderDevice>? device = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(project);

        services.AddJazzHandsEngine();

        // The selection prunes itself after every command, so a deleted clip never stays selected.
        services.AddSingleton(provider =>
        {
            var session = new Session(project, provider, path, recovery: true) { DefaultIssuer = "gui" };
            provider.GetRequiredService<SelectionService>().Attach(session);
            return session;
        });
        services.AddSingleton<ISession>(provider => new EngineSession(provider.GetRequiredService<Session>()));

        services.AddSingleton<IUiDispatcher, WpfDispatcher>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<IDialogService>(provider =>
            new DialogService(
                provider.GetRequiredService<ImportViewModel>,
                provider.GetRequiredService<ExportDialogViewModel>,
                provider.GetRequiredService<ViewModels.Settings.SettingsViewModel>,
                provider.GetRequiredService<MissingMediaViewModel>,
                provider.GetRequiredService<ConsolidateViewModel>,
                // Made outside the container: it is disposable, and a container keeps every disposable it
                // hands out until the editor quits, then disposes them again (a crash on quit, 2026-09-29).
                () => ActivatorUtilities.CreateInstance<SceneCutsViewModel>(provider)));
        services.AddTransient<MissingMediaViewModel>();
        services.AddTransient<ConsolidateViewModel>();

        services.AddTransient<ImportViewModel>();
        services.AddTransient<ExportDialogViewModel>();

        // The person's settings, a section each of %APPDATA%\JazzHands\settings.json, and the
        // Settings dialog over them, made fresh each time it opens so it reads what is there.
        // settings.get and settings.set reach these sections too; when one of them writes, the
        // section is read again, which applies what it can (the playback device, scrub sound).
        services.AddJazzHandsControlSettings();
        services.AddSingleton(new Engine.Settings.SettingsSectionType("editor", typeof(EditorSettings), "The editor's own preferences: playback, the GPU, export defaults, the window, closing and notifications"));
        services.AddSingleton(new Engine.Settings.SettingsSectionType("recent", typeof(RecentProjects), "The projects opened lately, newest first"));
        services.AddSingleton(provider => Reloaded(provider, EditorSettings.Store()));
        services.AddSingleton(provider => Reloaded(provider, Services.RecentProjects.Store()));
        services.AddSingleton<IPlaybackPreferences>(provider => new EnginePlaybackPreferences(
            provider.GetService<Transport>(),
            provider.GetService<Engine.Caching.ProxyService>()));
        services.AddTransient(provider => new ViewModels.Keys.KeymapEditorViewModel(
            provider.GetRequiredService<Input.KeymapService>(),
            provider.GetRequiredService<IFileDialogService>()));
        services.AddTransient(provider => new ViewModels.Settings.SettingsViewModel(
            provider.GetRequiredService<ISession>(),
            provider.GetRequiredService<Engine.Settings.SettingsSection<EditorSettings>>(),
            provider.GetRequiredService<Engine.Caching.CacheSettingsStore>(),
            provider.GetRequiredService<ControlSettingsStore>(),
            provider.GetRequiredService<IPlaybackPreferences>(),
            provider.GetRequiredService<ViewModels.Keys.KeymapEditorViewModel>(),
            SafeAdapters()));

        // The export queue: jobs from the dialog, the CLI with --attach and MCP alike, kept in
        // %LOCALAPPDATA%\JazzHands\queue.db so they outlive the window. It renders on a device of
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
            EditorSettings editor = provider.GetRequiredService<Engine.Settings.SettingsSection<EditorSettings>>().Current;
            Transport transport = Transport.ForDefaultDevice(settings.SampleRate, settings.ChannelCount, editor.AudioDevice);
            transport.ScrubAudio = editor.ScrubAudio;
            transport.Attach(provider.GetRequiredService<Session>());
            return transport;
        });
        services.AddSingleton(provider => new TransportMeters(provider.GetRequiredService<Transport>()));
        services.AddSingleton<IMeterFeed>(provider => new TransportMeterFeed(provider.GetRequiredService<TransportMeters>()));
        services.AddSingleton<IMixerFeed>(provider => new TransportMixerFeed(provider.GetRequiredService<TransportMeters>()));

        // One device for decode, the preview and everything else that touches the GPU. WARP when
        // there is no hardware adapter, so the editor still opens on a machine without one.
        services.AddSingleton(_ => device?.GetAwaiter().GetResult() ?? RenderDevice.Create());

        // The playback engine is what the playback.* commands drive: the session finds it among
        // these services as IPlaybackController, which is what makes a command from the CLI or
        // MCP move the same playhead as the space bar.
        services.AddSingleton(provider =>
        {
            Session session = provider.GetRequiredService<Session>();
            var engine = new PlaybackEngine(
                provider.GetRequiredService<Transport>(),
                provider.GetRequiredService<RenderDevice>(),
                options: Shell.SafeMode.IsOn ? new PlaybackOptions { HardwareDecode = false } : null,
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
            provider.GetRequiredService<PointPicker>(),
            provider.GetRequiredService<IDisplaySettings>(),
            new TitleHandlesViewModel(
                provider.GetRequiredService<ISession>(),
                provider.GetRequiredService<SelectionService>(),
                provider.GetRequiredService<IPreviewEngine>(),
                provider.GetRequiredService<IUiDispatcher>()),
            new MaskHandlesViewModel(
                provider.GetRequiredService<ISession>(),
                provider.GetRequiredService<SelectionService>(),
                provider.GetRequiredService<IPreviewEngine>(),
                provider.GetRequiredService<IUiDispatcher>()),
            new Gizmo3DViewModel(
                provider.GetRequiredService<ISession>(),
                provider.GetRequiredService<SelectionService>(),
                provider.GetRequiredService<IPreviewEngine>(),
                provider.GetRequiredService<IUiDispatcher>())));
        services.AddSingleton<IDisplaySettings>(_ => new FileDisplaySettings());

        // The source monitor (Phase 38): the session's SourceMonitor holds the item and marks,
        // and its player is a second engine on the same device, made on the first file opened.
        services.AddSingleton(provider =>
        {
            var player = new SourcePlayer(
                provider.GetRequiredService<Session>(),
                provider.GetRequiredService<RenderDevice>(),
                () => provider.GetRequiredService<Engine.Settings.SettingsSection<EditorSettings>>().Current.AudioDevice,
                Shell.SafeMode.IsOn ? new PlaybackOptions { HardwareDecode = false } : null,
                provider.GetService<Media.Import.CacheManager>());
            provider.GetRequiredService<SourceMonitor>().Player = player;
            return player;
        });
        services.AddSingleton(provider => new SourcePanelViewModel(
            provider.GetRequiredService<ISession>(),
            provider.GetRequiredService<SourceMonitor>(),
            provider.GetRequiredService<SourcePlayer>(),
            provider.GetRequiredService<IUiDispatcher>(),
            () => provider.GetRequiredService<IPreviewEngine>().Position,
            provider.GetRequiredService<IDisplaySettings>()));
        services.AddSingleton<Shell.IQuietWhileHidden>(provider => provider.GetRequiredService<SourcePanelViewModel>());

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
            provider.GetRequiredService<IPreviewEngine>(),
            dialogs: provider.GetRequiredService<IDialogService>()));

        services.AddSingleton(provider => new ViewModels.Grading.ColorPanelViewModel(
            provider.GetRequiredService<ISession>(),
            provider.GetRequiredService<SelectionService>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<IPreviewEngine>()));
        services.AddSingleton(provider => new ViewModels.Grading.ScopesPanelViewModel(
            provider.GetRequiredService<IPreviewEngine>(),
            provider.GetRequiredService<IUiDispatcher>()));

        services.AddSingleton<MediaPanelViewModel>();
        services.AddSingleton<MetersPanelViewModel>(provider => new MetersPanelViewModel(
            provider.GetRequiredService<IMeterFeed>(),
            provider.GetRequiredService<IUiDispatcher>()));
        services.AddSingleton(provider => new MixerPanelViewModel(
            provider.GetRequiredService<ISession>(),
            provider.GetRequiredService<IMixerFeed>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<IPreviewEngine>()));
        services.AddSingleton(provider => new ViewModels.Subtitles.SubtitlesPanelViewModel(
            provider.GetRequiredService<ISession>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<IFileDialogService>(),
            provider.GetRequiredService<IPreviewEngine>()));
        services.AddSingleton(provider => new ViewModels.Transcript.TranscriptPanelViewModel(
            provider.GetRequiredService<ISession>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<JazzHands.Engine.Caching.TranscriptionService>(),
            provider.GetRequiredService<IPreviewEngine>(),
            provider.GetRequiredService<SelectionService>()));
        services.AddSingleton<INodeViewer, NodeViewer>();
        services.AddSingleton(provider => new ViewModels.Comp.CompPanelViewModel(
            provider.GetRequiredService<ISession>(),
            provider.GetRequiredService<SelectionService>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<IPreviewEngine>(),
            provider.GetRequiredService<INodeViewer>()));
        // The control server: the pipe always, TCP when the settings say so. App starts it once
        // the window is up and disposes it before the session, so clients hear session.closed.
        services.AddSingleton(_ => new ControlSettingsStore());
        services.AddSingleton<Shell.AppHostMethods>();
        services.AddSingleton(provider => new ControlServer(
            new ControlTarget
            {
                Session = provider.GetRequiredService<Session>(),
                Selection = provider.GetRequiredService<SelectionService>(),
                Playback = provider.GetRequiredService<PlaybackEngine>(),
                Exports = provider.GetRequiredService<IExportService>(),
                Kind = "gui",
                HostMethods = provider.GetRequiredService<Shell.AppHostMethods>().Methods,
            },
            provider.GetRequiredService<ControlSettingsStore>().Current.ToOptions("Jazz Hands")));
        services.AddSingleton(provider => new ViewModels.Remote.CommandConsoleViewModel(
            provider.GetRequiredService<ControlServer>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<IFileDialogService>()));

        // Notifications: the corner toasts and the bell's history. What the engine notices on its own
        // (a decoder falling back, a file missing) reaches them as warnings.
        services.AddSingleton(provider =>
        {
            var notifications = new NotificationService(provider.GetRequiredService<IUiDispatcher>());
            provider.GetRequiredService<Session>().Notices.Raised += (_, notice) => notifications.Show(
                notice.Level switch { Core.Diagnostics.DiagnosticLevel.Error => NotificationLevel.Error, Core.Diagnostics.DiagnosticLevel.Warning => NotificationLevel.Warning, _ => NotificationLevel.Information },
                notice.Message);
            return notifications;
        });
        services.AddSingleton<INotificationService>(provider => provider.GetRequiredService<NotificationService>());
        services.AddSingleton(provider =>
        {
            IPreviewEngine preview = provider.GetRequiredService<IPreviewEngine>();
            TimelineDocuments timelines = provider.GetRequiredService<TimelineDocuments>();
            return new Shell.StatusBarViewModel(
                provider.GetRequiredService<ISession>(),
                provider.GetRequiredService<NotificationService>(),
                () => timelines.ActiveTimeline,
                () => preview.Position,
                provider.GetRequiredService<IExportService>(),
                provider.GetRequiredService<ControlServer>(),
                provider.GetRequiredService<RenderDevice>().AdapterName,
                provider.GetRequiredService<IUiDispatcher>());
        });

        // The shell's own panels: the undo history, the log the editor keeps in memory, the markers,
        // and the curve editor.
        services.AddSingleton(provider => new ViewModels.History.HistoryPanelViewModel(
            provider.GetRequiredService<ISession>(),
            provider.GetRequiredService<IUiDispatcher>()));
        services.AddSingleton(provider => new ViewModels.Logging.LogPanelViewModel(
            Engine.Logging.LogSetup.RingBuffer,
            provider.GetRequiredService<IUiDispatcher>(),
            text => System.Windows.Clipboard.SetText(text)));
        services.AddSingleton(provider =>
        {
            IPreviewEngine preview = provider.GetRequiredService<IPreviewEngine>();
            return new ViewModels.Markers.MarkersPanelViewModel(
                provider.GetRequiredService<ISession>(),
                provider.GetRequiredService<IUiDispatcher>(),
                () => preview.Position);
        });
        services.AddSingleton(provider =>
        {
            IPreviewEngine preview = provider.GetRequiredService<IPreviewEngine>();
            return new ViewModels.Curves.CurveEditorPanelViewModel(
                provider.GetRequiredService<ISession>(),
                provider.GetRequiredService<SelectionService>(),
                provider.GetRequiredService<IUiDispatcher>(),
                () => preview.Position);
        });

        // Desktop integration (Phase 27a): what the taskbar and the notification area show, what
        // goes quiet while the window is hidden, and the lifetime the app commands reach.
        services.AddSingleton(provider => new Shell.DesktopStatus(
            provider.GetService<IExportService>(),
            provider.GetRequiredService<IUiDispatcher>(),
            () => provider.GetService<Engine.Caching.ProxyService>()?.Folder,
            () => provider.GetService<ControlServer>()?.Clients ?? []));
        services.AddSingleton<Shell.IQuietWhileHidden>(provider => new Shell.QuietPreview(
            provider.GetService<PlaybackEngine>(),
            provider.GetService<PreviewPanelViewModel>()));
        services.AddSingleton<Shell.IQuietWhileHidden>(provider => provider.GetRequiredService<MetersPanelViewModel>());
        services.AddSingleton<Shell.IQuietWhileHidden>(provider => provider.GetRequiredService<MixerPanelViewModel>());
        services.AddSingleton(provider => new Shell.AppLifetime(
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<ISession>(),
            provider.GetService<IExportService>(),
            provider.GetRequiredService<Engine.Settings.SettingsSection<EditorSettings>>(),
            provider.GetServices<Shell.IQuietWhileHidden>(),
            () => System.Windows.Application.Current?.Shutdown(),
            Shell.Portable.MayRegister ? Shell.ShellRegistration.UnregisterCurrentUser : null));
        services.AddSingleton<Engine.Hosting.IAppController>(provider => provider.GetRequiredService<Shell.AppLifetime>());
        services.AddSingleton(provider => new Shell.TrayMenu(
            provider.GetRequiredService<ISession>(),
            provider.GetRequiredService<Shell.DesktopStatus>(),
            provider.GetRequiredService<Shell.AppLifetime>(),
            provider.GetRequiredService<Engine.Settings.SettingsSection<RecentProjects>>(),
            () => provider.GetService<Engine.Caching.ProxyService>()?.Enabled ?? false,
            async path =>
            {
                provider.GetRequiredService<Shell.AppLifetime>().Show();
                await provider.GetRequiredService<MainViewModel>().OpenAsync(path).ConfigureAwait(true);
            },
            panel =>
            {
                provider.GetRequiredService<Shell.AppLifetime>().Show();
                provider.GetRequiredService<MainViewModel>().ShowPanel(panel);
            }));
        services.AddSingleton(provider => new Shell.TrayHost(
            provider.GetRequiredService<Shell.DesktopStatus>(),
            provider.GetRequiredService<Shell.TrayMenu>(),
            provider.GetRequiredService<Shell.AppLifetime>()));

        services.AddSingleton<MainViewModel>();

        return services;
    }

    /// <summary>A settings section that reads itself again when settings.set writes to it.</summary>
    private static Engine.Settings.SettingsSection<T> Reloaded<T>(IServiceProvider provider, Engine.Settings.SettingsSection<T> section)
        where T : class, new()
    {
        provider.GetRequiredService<Engine.Settings.SettingsCatalog>().Changed += (_, name) =>
        {
            if (name == section.Section)
            {
                section.Reload();
            }
        };
        return section;
    }

    // The adapters for the Settings dialog's GPU list; none when DXGI will not say, so the dialog still opens.
    private static IReadOnlyList<string> SafeAdapters()
    {
        try
        {
            return Render.RenderDevice.Adapters();
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or SharpGen.Runtime.SharpGenException or DllNotFoundException)
        {
            Serilog.Log.Warning(error, "The GPU adapters could not be listed");
            return [];
        }
    }
}
