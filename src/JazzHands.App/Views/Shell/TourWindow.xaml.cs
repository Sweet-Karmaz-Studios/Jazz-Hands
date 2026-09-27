using System.Windows;

namespace JazzHands.App.Views.Shell;

/// <summary>The tour's window: eight short cards, one at a time.</summary>
public partial class TourWindow : Window
{
    /// <summary>Creates the window.</summary>
    public TourWindow() => InitializeComponent();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
