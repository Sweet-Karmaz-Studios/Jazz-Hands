using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace JazzHands.App.Views.Audio;

/// <summary>The Audio Mixer: a strip per audio track and the master.</summary>
public partial class MixerPanelView : UserControl
{
    /// <summary>Creates the view.</summary>
    public MixerPanelView() => InitializeComponent();

    /// <summary>Opens a strip's effect menu under its button, on a left click rather than only a right one.</summary>
    private void OnAddEffectClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
