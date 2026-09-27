using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace JazzHands.App.Views.Grading;

/// <summary>The colour wheels and curves.</summary>
public partial class ColorPanelView : UserControl
{
    /// <summary>Creates the panel.</summary>
    public ColorPanelView() => InitializeComponent();

    /// <summary>"Match to...", "+ Serial" and "+ Parallel" drop their lists down under them.</summary>
    private void OnDropDownClick(object sender, RoutedEventArgs e) => OnMatchClick(sender, e);

    /// <summary>"Match to..." drops its list of clips down under it.</summary>
    private void OnMatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
