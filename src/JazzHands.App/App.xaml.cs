using System.IO;
using System.Windows;
using JazzHands.App.Services;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Logging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;

namespace JazzHands.App;

/// <summary>
/// The WPF application: logging, the service provider, the session, the window and the control
/// server that lets <c>jazz --attach</c> and MCP drive it.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;
    private Shell.TrayHost? _tray;
    private Shell.StartupTimes? _startup;
    private bool _measuring;
    private readonly Task<Render.RenderDevice>? _device;
    private int _crashed;
    private Mutex? _single;

    /// <summary>
    /// Starts creating the render device before anything else: the GPU chosen in Settings, unless
    /// JAZZ_GPU says otherwise for this run, on another thread while WPF loads its resources and
    /// the rest starts. Creating it takes about 220 ms that nothing waits on until the playback
    /// engine is made (Phase 32). A spike makes its own.
    /// </summary>
    public App()
    {
        if (Environment.GetCommandLineArgs().Contains("--spike", StringComparer.Ordinal))
        {
            return;
        }

        // Safe mode draws on WARP whatever Settings say (Phase 33).
        if (!Shell.SafeMode.Apply(Environment.GetCommandLineArgs())
            && Environment.GetEnvironmentVariable("JAZZ_GPU") is null
            && Render.GpuChoice.TryParse(EditorSettings.Store().Current.Gpu, out Render.GpuChoice? gpu))
        {
            Render.RenderDevice.Preferred = gpu;
        }

        _device = Task.Run(() => Render.RenderDevice.Create());
    }

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        _startup = new Shell.StartupTimes();
        _startup.Mark("runtime");

        bool spike = e.Args.Contains("--spike", StringComparer.Ordinal);
        LogSetup.ConfigureForApp(spike ? LogEventLevel.Debug : LogEventLevel.Information);
        Log.ForContext<App>().Information("Jazz Hands starting");
        if (Shell.SafeMode.IsOn)
        {
            Log.ForContext<App>().Warning("Safe mode: WARP, software decoding, the built-in layout, an empty cache and no custom transitions");
        }
        else
        {
            Engine.Effects.EffectCatalog.LoadUserTransitions();
        }

        // Nothing should die without saying why. Phase 33 adds the recovery save and the last
        // fifty commands; this is the floor.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            Crash(args.Exception, ask: true);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            // The runtime ends the process when this returns; there is time to save, not to ask.
            Crash(args.ExceptionObject as Exception ?? new InvalidOperationException("An unknown error on a background thread"), ask: false);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.ForContext<App>().Error(args.Exception, "Unobserved task exception");
        };

        base.OnStartup(e);

        EditorSettings editor = EditorSettings.Store().Current;

        if (Shell.StartupTimes.MeasurePath(e.Args) is { } measured)
        {
            MeasureStartup(measured);
            return;
        }

        // The taskbar, the jump list and notifications key on this; it must come before any window.
        if (!spike)
        {
            Shell.ShellRegistration.SetProcessAppId();
        }

        // One editor at a time: a second launch hands what it was asked (a project, a Quick Trim,
        // a notification's link) to the first and goes, whatever the first's window is doing.
        Shell.LaunchRequest launch = Shell.LaunchRequest.Parse(e.Args);
        string? project = spike ? null
            : launch.Action is Shell.LaunchAction.Show or Shell.LaunchAction.Background
                ? Shell.Startup.ProjectToOpen(e.Args, editor, Services.RecentProjects.Store().Current)
                : null;
        bool alone = spike || e.Args.Contains(Shell.Startup.NewInstance, StringComparer.Ordinal) || Shell.SafeMode.IsOn;
        bool first = true;
        if (!alone)
        {
            _single = Shell.Startup.Claim(out first);
        }

        if (!alone
            && (Shell.Startup.TryHandOffAsync(launch).GetAwaiter().GetResult()
                || (!first && Shell.Startup.HandOffToTheFirstAsync(launch, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult())))
        {
            _device?.ContinueWith(made => made.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
            Shutdown(0);
            return;
        }

        // The window is a view of the session: closing it hides it, and only a quit ends the process.
        if (!spike)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        try
        {
            MainWindow = CreateStartupWindow(project, spike, e.Args);
            if (launch.Action != Shell.LaunchAction.Background || spike)
            {
                MainWindow.ContentRendered += OnFirstFrame;
                MainWindow.Show();
                _startup.Mark("shown");
            }

            StartControlServer();
            AfterStart(launch);
        }
        catch (Exception ex)
        {
            // A WinExe that throws during startup dies without a word, which is a miserable way
            // to find out the build is wrong. Say something before going.
            Log.ForContext<App>().Fatal(ex, "Jazz Hands could not start");
            MessageBox.Show(
                ex.Message,
                "Jazz Hands could not start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// The end of the road for an error nothing could handle: the unsaved work saved for recovery,
    /// a report with the last fifty commands beside the logs, and, on the UI thread, a word with
    /// the person and the choice to start again, where the work is offered back (Phase 33).
    /// </summary>
    private void Crash(Exception error, bool ask)
    {
        if (Interlocked.Exchange(ref _crashed, 1) == 1)
        {
            return;
        }

        Engine.Commands.Session? session = null;
        try
        {
            session = _services?.GetService<Engine.Commands.Session>();
        }
        catch (Exception resolving) when (resolving is InvalidOperationException or ObjectDisposedException)
        {
            // Still being built, or already gone: there is nothing of the session to save.
        }

        (string? report, string? rescued) = Engine.Recovery.CrashReport.Write(error, session);
        Log.CloseAndFlush();

        if (!ask)
        {
            return;
        }

        string saved = rescued is null
            ? "There were no unsaved changes."
            : "Your unsaved work was saved, and is offered back when Jazz Hands starts again.";
        string where = report is null ? string.Empty : $"\n\nWhat happened, with the last fifty commands, is in {report}.";

        MessageBoxResult answer = MessageBox.Show(
            $"Jazz Hands met a problem it cannot carry on from, and has to close.\n\n{saved}{where}\n\nStart it again now?",
            "Jazz Hands has to close",
            MessageBoxButton.YesNo,
            MessageBoxImage.Error);

        if (answer == MessageBoxResult.Yes && Environment.ProcessPath is { } exe)
        {
            var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
            start.ArgumentList.Add(Shell.Startup.NewInstance);
            if (session?.ProjectPath is { Length: > 0 } project)
            {
                start.ArgumentList.Add(project);
            }

            using (System.Diagnostics.Process.Start(start))
            {
            }
        }

        Environment.Exit(1);
    }

    /// <summary>Logs how long starting took, once the window's first frame is up.</summary>
    private void OnFirstFrame(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.ContentRendered -= OnFirstFrame;
        }

        if (_startup is { } startup)
        {
            startup.Mark("first frame");
            Log.ForContext<App>().Information("Jazz Hands ready: {Steps} after the process started", startup.Describe());
        }
    }

    /// <summary>
    /// Starts as a launch with no project does, with the window built and laid out off screen and
    /// never shown, writes how long each step took to <paramref name="path"/>, and quits. Nothing
    /// is handed to another editor, registered with Windows or put in the notification area.
    /// </summary>
    private void MeasureStartup(string path)
    {
        Shell.StartupTimes startup = _startup!;
        _measuring = true;
        try
        {
            Window window = CreateStartupWindow(null, spike: false, []);
            if (window.Content is FrameworkElement content)
            {
                var size = new Size(2560, 1440);
                content.Measure(size);
                content.Arrange(new Rect(size));
                content.UpdateLayout();
            }

            startup.Mark("layout");
            startup.Write(path);
            Log.ForContext<App>().Information("Measured a start without showing the window: {Steps}", startup.Describe());
            Shutdown(0);
        }
        catch (Exception error)
        {
            Log.ForContext<App>().Fatal(error, "Measuring the start failed");
            Shutdown(1);
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        Log.ForContext<App>().Information("Jazz Hands exiting with code {ExitCode}", e.ApplicationExitCode);

        // The session owns the autosave timer, the history log and the probe cache's SQLite
        // connection. Blocking here is the one place it is right: the process is going away and
        // an unflushed autosave is lost work.
        // The notification area icon goes first, so nothing is left in the taskbar that clicks to
        // nowhere. The control server next, so its clients hear session.closed while the session
        // is still there to describe.
        _tray?.Dispose();
        _services?.GetService<Control.ControlServer>()?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _services?.GetService<Session>()?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        // Asynchronously: the control server can only be disposed that way, and a synchronous
        // dispose of the container throws for it.
        _services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _single?.Dispose();

        Shell.SafeMode.Clean();
        LogSetup.Shutdown();
        base.OnExit(e);
    }

    /// <summary>
    /// Opens the project named on the command line, or an empty one.
    /// </summary>
    /// <remarks>
    /// A file that will not load is reported and replaced with an empty project rather than
    /// taking the application down, because the alternative is an editor that cannot be started
    /// to fix the file that stops it starting.
    /// </remarks>
    private static (Project Project, string Path) OpenProject(string? path)
    {
        if (path is null)
        {
            return (Project.CreateNew("Untitled"), string.Empty);
        }

        try
        {
            string full = System.IO.Path.GetFullPath(path);
            return (ProjectFile.Load(full).Project, full);
        }
        catch (Exception error) when (error is ProjectFileException or IOException)
        {
            Log.ForContext<App>().Error(error, "Could not open {Path}", path);
            MessageBox.Show(error.Message, "Could not open the project", MessageBoxButton.OK, MessageBoxImage.Warning);
            return (Project.CreateNew("Untitled"), string.Empty);
        }
    }

    /// <summary>
    /// Starts listening for remote clients. A server that cannot start (the pipe refused, the TCP
    /// port taken) is logged and the editor carries on: remote control is worth having, not worth
    /// refusing to edit over.
    /// </summary>
    private void StartControlServer()
    {
        if (_services?.GetService<Control.ControlServer>() is not { } server)
        {
            return;
        }

        try
        {
            server.StartAsync().GetAwaiter().GetResult();
            _services.GetService<ViewModels.Remote.CommandConsoleViewModel>()?.ServerStarted();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            Log.ForContext<App>().Error(error, "The control server could not start; remote control is off for this run");
        }
    }

    /// <summary>
    /// What follows the window: the notification area and the lifetime, the method a second
    /// launch calls, Windows' side of things (the file types, the jump list, notifications), the
    /// project on the recent list, the preview quality Settings chose, and what this launch asked.
    /// </summary>
    private void AfterStart(Shell.LaunchRequest launch)
    {
        if (_services is null || MainWindow is not MainWindow window)
        {
            return;
        }

        ViewModels.MainViewModel model = _services.GetRequiredService<ViewModels.MainViewModel>();
        Session session = _services.GetRequiredService<Session>();
        Engine.Settings.SettingsSection<EditorSettings> editor = _services.GetRequiredService<Engine.Settings.SettingsSection<EditorSettings>>();
        Engine.Settings.SettingsSection<RecentProjects> recent = _services.GetRequiredService<Engine.Settings.SettingsSection<RecentProjects>>();
        Shell.AppLifetime lifetime = _services.GetRequiredService<Shell.AppLifetime>();
        Shell.DesktopStatus desktop = _services.GetRequiredService<Shell.DesktopStatus>();

        _tray = _services.GetRequiredService<Shell.TrayHost>();
        lifetime.Attach(window, _tray.Notify, model.ReadyToCloseAsync, _services.GetRequiredService<IDialogService>());
        if (launch.Action == Shell.LaunchAction.Background)
        {
            lifetime.StartHidden();
        }

        _services.GetRequiredService<Shell.AppHostMethods>().Methods["app.launch"] = args => Dispatcher.InvokeAsync(async () =>
        {
            await HandleLaunchAsync(Shell.LaunchRequest.FromJson(args)).ConfigureAwait(true);
            return (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject { ["ok"] = true };
        }).Task.Unwrap();

        // Windows notifications, with the notification area's balloon when Windows will not.
        var notifications = new Shell.DesktopNotifications(() => editor.Current.WindowsNotifications, _tray.Notify);
        desktop.ExportFinished += (_, job) => notifications.ExportFinished(job);
        desktop.ProxiesFinished += (_, count) => notifications.ProxiesReady(count);
        desktop.ClientArrived += (_, client) =>
        {
            if (editor.Current.NotifyClientAttached)
            {
                notifications.ClientAttached(client);
            }
        };

        // Clients come and go without a queue event; the status looks every two seconds.
        var clients = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        clients.Tick += (_, _) => desktop.Refresh();
        clients.Start();

        // Windows' side: the .jazz type, the jazzhands: links, the Explorer verbs, starting with Windows.
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "JazzHands.exe");
        var registration = new Shell.ShellRegistration(new Shell.CurrentUserRegistry());
        try
        {
            // An isolated run (JAZZ_HOME: the UI tests) leaves the person's Windows alone.
            if (!Core.JazzFolders.IsIsolated && registration.Register(exe, Path.Combine(AppContext.BaseDirectory, "Assets")))
            {
                Shell.ShellRegistration.NotifyExplorer();
            }

            if (!Core.JazzFolders.IsIsolated && registration.StartsWithWindows != editor.Current.StartWithWindows)
            {
                registration.SetStartWithWindows(editor.Current.StartWithWindows, exe);
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.ForContext<App>().Warning(error, "Windows could not be told about Jazz Hands; .jazz files and the Explorer verbs may not work");
        }

        // Settings that apply at once, whoever changed them: the dialog, or settings.set.
        IPlaybackPreferences playback = _services.GetRequiredService<IPlaybackPreferences>();
        editor.Saved += (_, settings) => Dispatcher.InvokeAsync(() =>
        {
            if (playback.Device != settings.AudioDevice)
            {
                playback.Device = settings.AudioDevice;
            }

            playback.ScrubAudio = settings.ScrubAudio;
            try
            {
                if (registration.StartsWithWindows != settings.StartWithWindows)
                {
                    registration.SetStartWithWindows(settings.StartWithWindows, exe);
                }
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                Log.ForContext<App>().Warning(error, "Starting with Windows could not be changed");
            }
        });

        // The jump list follows the recent projects; an isolated run leaves the person's alone.
        void ApplyJumpList(RecentProjects projects)
        {
            if (!Core.JazzFolders.IsIsolated)
            {
                System.Windows.Shell.JumpList.SetJumpList(this, Shell.JumpLists.Build(projects.Paths, exe, LogSetup.DefaultLogDirectory));
            }
        }

        recent.Saved += (_, projects) => Dispatcher.InvokeAsync(() => ApplyJumpList(projects));

        if (session.ProjectPath.Length > 0)
        {
            recent.Update(projects => projects.With(session.ProjectPath));
            model.BuildMenu();
        }
        else
        {
            ApplyJumpList(recent.Current);
        }

        // Work a crash left is offered once the window is up; started in the background, the
        // notification says so and the offer waits for the window.
        if (launch.Action == Shell.LaunchAction.Background)
        {
            if (session.ProjectPath.Length > 0 && new Engine.Recovery.RecoveryService().Find(session.ProjectPath) is { } offer)
            {
                notifications.RecoveryAvailable(session.ProjectPath, offer.Describe());
            }
        }
        else
        {
            _ = Dispatcher.InvokeAsync(model.OfferRecoveryAsync, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        if (editor.Current.PreviewQuality != "auto" && Enum.TryParse(editor.Current.PreviewQuality, ignoreCase: true, out Core.Commands.PreviewQuality quality))
        {
            _ = session.ExecuteAsync(new Core.Commands.SetQualityCommand(quality));
        }

        if (launch.Action is not (Shell.LaunchAction.Show or Shell.LaunchAction.Background))
        {
            _ = HandleLaunchAsync(launch);
        }
    }

    /// <summary>
    /// Does what a launch asked: this one's, or a second launch's handed over as <c>app.launch</c>.
    /// </summary>
    private async Task HandleLaunchAsync(Shell.LaunchRequest launch)
    {
        if (_services is null)
        {
            return;
        }

        ViewModels.MainViewModel model = _services.GetRequiredService<ViewModels.MainViewModel>();
        Shell.AppLifetime lifetime = _services.GetRequiredService<Shell.AppLifetime>();
        if (launch.Action is not (Shell.LaunchAction.Background or Shell.LaunchAction.Reveal))
        {
            lifetime.Show();
        }

        switch (launch.Action)
        {
            case Shell.LaunchAction.Show or Shell.LaunchAction.Background when launch.Project is { } project:
                await model.OpenAsync(project).ConfigureAwait(true);
                break;

            case Shell.LaunchAction.NewProject:
                await model.NewProjectAsync().ConfigureAwait(true);
                break;

            case Shell.LaunchAction.QuickTrim when launch.Files.Count > 0:
                await model.QuickTrimAsync(launch.Files[0]).ConfigureAwait(true);
                break;

            case Shell.LaunchAction.QuickTrim:
                model.QuickTrimFileCommand.Execute(null);
                break;

            case Shell.LaunchAction.AddMedia when launch.Files.Count > 0:
                await _services.GetRequiredService<IDialogService>().ShowImportAsync(launch.Files).ConfigureAwait(true);
                break;

            case Shell.LaunchAction.ExportQueue:
                model.ShowPanel("exportQueue");
                _services.GetRequiredService<Shell.DesktopStatus>().Acknowledge();
                break;

            case Shell.LaunchAction.Reveal when launch.Files.Count > 0 && File.Exists(launch.Files[0]):
                using (System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{launch.Files[0]}\""))
                {
                }

                break;
        }
    }

    private Window CreateStartupWindow(string? projectPath, bool spike, string[] args)
    {
#if SPIKES
        if (spike)
        {
            return new Spikes.SpikeWindow(Spikes.SpikeOptions.Parse(args));
        }
#else
        if (spike)
        {
            throw new InvalidOperationException(
                "This build does not include the spike harnesses, because JazzSpikes was not set "
                + "when it was built. Rebuild with:"
                + Environment.NewLine
                + Environment.NewLine
                + "    dotnet build src\\JazzHands.App -c Release -p:JazzSpikes=true");
        }
#endif
        (Project project, string path) = OpenProject(projectPath);
        _startup?.Mark("project");

        var services = new ServiceCollection();
        services.AddJazzHandsApp(project, path, _device);
        _services = services.BuildServiceProvider();
        if (_measuring)
        {
            // Measuring: the heavy services one at a time first, so the times say which is slow.
            _services.GetRequiredService<Session>();
            _startup?.Mark("session");
            _services.GetRequiredService<Render.RenderDevice>();
            _startup?.Mark("device");
            _services.GetRequiredService<Engine.Playback.Transport>();
            _startup?.Mark("transport");
            _services.GetRequiredService<Engine.Playback.PlaybackEngine>();
            _startup?.Mark("playback");
            _services.GetRequiredService<Engine.Export.ExportQueue>();
            _startup?.Mark("export queue");
        }

        ViewModels.MainViewModel model = _services.GetRequiredService<ViewModels.MainViewModel>();
        _startup?.Mark("services");

        var window = new MainWindow(model);
        _startup?.Mark("window");
        return window;
    }
}
