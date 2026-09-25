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

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        bool spike = e.Args.Contains("--spike", StringComparer.Ordinal);
        LogSetup.ConfigureForApp(spike ? LogEventLevel.Debug : LogEventLevel.Information);
        Log.ForContext<App>().Information("Jazz Hands starting");
        Engine.Effects.EffectCatalog.LoadUserTransitions();

        // Nothing should die without saying why. Phase 33 adds the recovery save and the last
        // fifty commands; this is the floor.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.ForContext<App>().Fatal(args.Exception, "Unhandled exception on the UI thread");
            Log.CloseAndFlush();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.ForContext<App>().Fatal(args.ExceptionObject as Exception, "Unhandled exception on a background thread");
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.ForContext<App>().Error(args.Exception, "Unobserved task exception");
        };

        base.OnStartup(e);

        // One editor at a time: a second launch hands its project to the first and goes.
        EditorSettings editor = EditorSettings.Store().Current;
        string? project = spike ? null : Shell.Startup.ProjectToOpen(e.Args, editor, Services.RecentProjects.Store().Current);
        if (!spike && !e.Args.Contains(Shell.Startup.NewInstance, StringComparer.Ordinal)
            && Shell.Startup.TryHandOffAsync(project).GetAwaiter().GetResult())
        {
            Shutdown(0);
            return;
        }

        // The GPU chosen in Settings, unless JAZZ_GPU says otherwise for this run.
        if (Environment.GetEnvironmentVariable("JAZZ_GPU") is null && Render.GpuChoice.TryParse(editor.Gpu, out Render.GpuChoice? gpu))
        {
            Render.RenderDevice.Preferred = gpu;
        }

        try
        {
            MainWindow = CreateStartupWindow(project, spike, e.Args);
            MainWindow.Show();
            StartControlServer();
            AfterStart(editor);
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

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        Log.ForContext<App>().Information("Jazz Hands exiting with code {ExitCode}", e.ApplicationExitCode);

        // The session owns the autosave timer, the history log and the probe cache's SQLite
        // connection. Blocking here is the one place it is right: the process is going away and
        // an unflushed autosave is lost work.
        // The control server goes first so its clients hear session.closed while the session is
        // still there to describe.
        _services?.GetService<Control.ControlServer>()?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _services?.GetService<Session>()?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _services?.Dispose();

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
    /// What follows the window: the methods a second launch calls, the project on the recent
    /// list, and the preview quality Settings chose.
    /// </summary>
    private void AfterStart(EditorSettings editor)
    {
        if (_services is null || MainWindow is not MainWindow window)
        {
            return;
        }

        ViewModels.MainViewModel model = _services.GetRequiredService<ViewModels.MainViewModel>();
        Dictionary<string, Func<System.Text.Json.Nodes.JsonObject, Task<System.Text.Json.Nodes.JsonNode?>>> methods = _services.GetRequiredService<Shell.AppHostMethods>().Methods;
        methods["app.activate"] = _ => Dispatcher.InvokeAsync(() =>
        {
            window.BringToFront();
            return (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject { ["ok"] = true };
        }).Task;
        methods["app.open"] = args => Dispatcher.InvokeAsync(async () =>
        {
            window.BringToFront();
            string path = args["path"]?.GetValue<string>() ?? throw new Control.JsonRpcException(Control.JsonRpc.InvalidParams, "app.open needs a path.");
            bool opened = await model.OpenAsync(path).ConfigureAwait(true);
            return (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject { ["opened"] = opened };
        }).Task.Unwrap();

        Session session = _services.GetRequiredService<Session>();
        if (session.ProjectPath.Length > 0)
        {
            _services.GetRequiredService<Engine.Settings.SettingsSection<Services.RecentProjects>>().Update(recent => recent.With(session.ProjectPath));
            model.BuildMenu();
        }

        if (editor.PreviewQuality != "auto" && Enum.TryParse(editor.PreviewQuality, ignoreCase: true, out Core.Commands.PreviewQuality quality))
        {
            _ = session.ExecuteAsync(new Core.Commands.SetQualityCommand(quality));
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

        var services = new ServiceCollection();
        services.AddJazzHandsApp(project, path);
        _services = services.BuildServiceProvider();

        return new MainWindow(_services.GetRequiredService<ViewModels.MainViewModel>());
    }
}
