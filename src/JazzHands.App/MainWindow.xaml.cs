using System.Windows;
using JazzHands.App.ViewModels;

namespace JazzHands.App;

/// <summary>
/// The shell window. Phase 27 fills it out with every panel, the workspaces and the layout
/// service; today it holds the docking manager and the media panel.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Creates the window.</summary>
    /// <param name="model">The window's viewmodel, from the host.</param>
    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }
}
