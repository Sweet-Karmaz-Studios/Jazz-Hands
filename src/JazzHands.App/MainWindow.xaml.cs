using System.Windows;

namespace JazzHands.App;

/// <summary>
/// The shell window. Phase 27 fills it with the docking layout; today it proves the app starts
/// and that the engine assemblies load in a WPF process.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Creates the window.</summary>
    public MainWindow()
    {
        InitializeComponent();
        Banner.Text = "Jazz Hands\nThe shell arrives in Phase 27.";
    }
}
