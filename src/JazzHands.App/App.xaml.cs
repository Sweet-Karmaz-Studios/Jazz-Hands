using System.Windows;
using JazzHands.Engine.Logging;
using Serilog;
using Serilog.Events;

namespace JazzHands.App;

/// <summary>
/// The WPF application. Phase 27 turns this into a Generic Host that owns the engine session,
/// the docking shell and the control server; for now it brings up logging and chooses a window.
/// </summary>
public partial class App : Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        bool spike = e.Args.Contains("--spike", StringComparer.Ordinal);
        LogSetup.ConfigureForApp(spike ? LogEventLevel.Debug : LogEventLevel.Information);
        Log.ForContext<App>().Information("Jazz Hands starting");

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
        LogSetup.Shutdown();
        base.OnExit(e);
    }

    private static Window CreateStartupWindow(string[] args, bool spike)
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
        return new MainWindow();
    }
}
