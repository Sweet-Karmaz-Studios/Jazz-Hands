using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JazzHands.App.ViewModels.Transcript;

namespace JazzHands.App.Views.Transcript;

/// <summary>The Transcript panel's view. A word is clicked here; Shift extends the selection.</summary>
public partial class TranscriptPanelView : UserControl
{
    /// <summary>Creates the view.</summary>
    public TranscriptPanelView() => InitializeComponent();

    private void OnWordClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TranscriptWordViewModel word })
        {
            word.Click(extend: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);

            // Delete reaches the panel's key binding once it has the keyboard.
            Focus();
            e.Handled = true;
        }
    }
}
