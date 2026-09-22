using System.Windows;
using JazzHands.Engine.Logging;
using Serilog;

namespace JazzHands.App;

/// <summary>
/// The WPF application. Phase 27 turns this into a Generic Host that owns the engine session,
/// the docking shell and the control server; for now it only brings up logging.
/// </summary>
public partial class App : Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        LogSetup.ConfigureForApp();
        Log.ForContext<App>().Information("Jazz Hands starting");
        base.OnStartup(e);
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        Log.ForContext<App>().Information("Jazz Hands exiting with code {ExitCode}", e.ApplicationExitCode);
        LogSetup.Shutdown();
        base.OnExit(e);
    }
}
