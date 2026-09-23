using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using JazzHands.App.ViewModels.Timeline;

namespace JazzHands.App.Views.Timeline;

/// <summary>A sequence's timeline: headers, the drawing, scroll bars and zoom.</summary>
/// <remarks>
/// The code here is view logic only: committing a header name on Enter or when focus leaves,
/// and turning a header's edge drag into a height preview and, on release, a command.
/// </remarks>
public partial class TimelineView : UserControl
{
    /// <summary>Creates the view.</summary>
    public TimelineView()
    {
        InitializeComponent();
    }

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TrackHeaderViewModel header } box)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            header.CommitNameCommand.Execute(null);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Put the name back by committing an unchanged one, which the header restores.
            box.Text = string.Empty;
            header.CommitNameCommand.Execute(null);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnNameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: TrackHeaderViewModel header })
        {
            header.CommitNameCommand.Execute(null);
        }
    }

    private void OnHeightDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is Thumb { DataContext: TrackHeaderViewModel header })
        {
            header.PreviewHeight(header.Height + e.VerticalChange);
        }
    }

    private async void OnHeightDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is Thumb { DataContext: TrackHeaderViewModel header })
        {
            await header.CommitHeightAsync().ConfigureAwait(true);
        }
    }
}
