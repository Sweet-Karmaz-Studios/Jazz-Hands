using System.Windows.Controls;
using System.Windows.Input;

namespace JazzHands.App.Views.Subtitles;

/// <summary>The Subtitles panel: a subtitle track's cues, editable, with its tools and style.</summary>
public partial class SubtitlesPanelView : UserControl
{
    /// <summary>Creates the view.</summary>
    public SubtitlesPanelView() => InitializeComponent();

    private void OnCueFocused(object sender, KeyboardFocusChangedEventArgs e) => ((ListBoxItem)sender).IsSelected = true;
}
