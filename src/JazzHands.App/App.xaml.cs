using System.IO;
using System.Windows;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Logging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;

namespace JazzHands.App;

/// <summary>
/// The WPF application. Phase 27 adds the control server and the workspaces; today it brings up
/// logging, the service provider, the session and the window.
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

        try
        {
            MainWindow = CreateStartupWindow(e.Args, spike);
            MainWindow.Show();
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
    private static (Project Project, string Path) OpenProject(string[] args)
    {
        string? path = args.FirstOrDefault(argument => argument.EndsWith(".jazz", StringComparison.OrdinalIgnoreCase));

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

    private Window CreateStartupWindow(string[] args, bool spike)
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
        (Project project, string path) = OpenProject(args);

        var services = new ServiceCollection();
        services.AddJazzHandsApp(project, path);
        _services = services.BuildServiceProvider();

        return new MainWindow(_services.GetRequiredService<ViewModels.MainViewModel>());
    }
}
