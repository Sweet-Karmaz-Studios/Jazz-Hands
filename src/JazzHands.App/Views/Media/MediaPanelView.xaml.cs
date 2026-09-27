using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JazzHands.App.Services;
using JazzHands.App.ViewModels.Media;

namespace JazzHands.App.Views.Media;

/// <summary>
/// The media panel.
/// </summary>
/// <remarks>
/// The code-behind is the mouse gesture that starts a drag, the tree selection that has no
/// bindable property, and where the mouse is across a tile for the hover scrub. All three are
/// view concerns that WPF gives no declarative way to express; everything they decide is handed
/// to the viewmodel.
/// </remarks>
public partial class MediaPanelView : UserControl
{
    private Point _pressed;
    private bool _pressedOnItem;

    /// <summary>Creates the panel.</summary>
    public MediaPanelView() => InitializeComponent();

    private MediaPanelViewModel? Model => DataContext as MediaPanelViewModel;

    /// <summary>
    /// Files dragged in from Explorer: a copy is offered, and a drop imports them. A drag out of
    /// this panel carries its files too, and is refused, so a clip is not imported twice.
    /// </summary>
    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(MediaDragData.Format) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths ? [] : paths;

    private void OnFilesOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFiles(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        string[] files = DroppedFiles(e);
        e.Handled = true;
        if (files.Length > 0 && Model is { } model)
        {
            _ = model.ImportDroppedAsync(files);
        }
    }

    private void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Model is { } model)
        {
            model.SelectedFolder = e?.NewValue as MediaFolderViewModel;
        }
    }

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = e?.GetPosition(this) ?? default;

        // Only a press that landed on a row starts a drag. A press on the header or the empty
        // space below the last row is a click, not the beginning of one.
        _pressedOnItem = e?.OriginalSource is DependencyObject source && FindRow(source) is not null;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressedOnItem || e?.LeftButton != MouseButtonState.Pressed || Model is not { } model)
        {
            return;
        }

        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - _pressed.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _pressed.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressedOnItem = false;

        MediaItemViewModel[] selection = sender is ListBox list
            ? [.. list.SelectedItems.OfType<MediaItemViewModel>()]
            : [];

        if (selection.Length == 0 && model.SelectedItem is { } single)
        {
            selection = [single];
        }

        if (selection.Length == 0)
        {
            return;
        }

        DragDrop.DoDragDrop(
            this,
            MediaDragData.Create(model.DragIds(selection), model.DragPaths(selection)),
            DragDropEffects.Copy | DragDropEffects.Link);
    }

    /// <summary>Hover scrub: how far across the tile the mouse is picks the frame shown.</summary>
    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaItemViewModel row, ActualWidth: > 0 } tile && Model is { } model)
        {
            model.ScrubTo(row, e.GetPosition(tile).X / tile.ActualWidth);
        }
    }

    private void OnTileMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaItemViewModel row } && Model is { } model)
        {
            model.EndScrub(row);
        }
    }

    private static ListBoxItem? FindRow(DependencyObject? from)
    {
        while (from is not null and not ListBoxItem)
        {
            from = System.Windows.Media.VisualTreeHelper.GetParent(from);
        }

        return from as ListBoxItem;
    }
}
