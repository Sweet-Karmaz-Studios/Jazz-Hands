using System.Windows.Input;

namespace JazzHands.App.Shell;

/// <summary>
/// What a panel can ask of the main window without knowing about it: routed commands, which the
/// main window answers (<c>MainWindow.CommandBindings</c>). Outside the window, as in a test that
/// draws a panel on its own, nothing answers and the button is simply disabled.
/// </summary>
public static class ShellCommands
{
    /// <summary>Help, Open the sample project.</summary>
    public static RoutedUICommand OpenSample { get; } = new("Open the sample project", nameof(OpenSample), typeof(ShellCommands));

    /// <summary>Help, Tour of the editor.</summary>
    public static RoutedUICommand Tour { get; } = new("Tour of the editor", nameof(Tour), typeof(ShellCommands));
}
