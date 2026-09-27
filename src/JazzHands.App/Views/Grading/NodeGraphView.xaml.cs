using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JazzHands.App.ViewModels.Grading;

namespace JazzHands.App.Views.Grading;

/// <summary>
/// The Colour panel's node view: draws the view model's nodes and connections, and turns a click
/// into a selection and a drag between nodes into <see cref="ColorPanelViewModel.Connect"/>.
/// </summary>
public partial class NodeGraphView : UserControl
{
    private NodeViewModel? _dragFrom;

    /// <summary>Creates the view.</summary>
    public NodeGraphView() => InitializeComponent();

    private ColorPanelViewModel? Panel => DataContext as ColorPanelViewModel;

    private void OnNodeDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NodeViewModel node })
        {
            Panel?.SelectNodeCommand.Execute(node);
            e.Handled = true;
        }
    }

    private void OnPortDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NodeViewModel node })
        {
            _dragFrom = node;
            Surface.CaptureMouse();
            DragLine.Data = NodeLinkGeometry.Curve(node.Out, e.GetPosition(Surface), key: false);
            e.Handled = true;
        }
    }

    private void OnSurfaceMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is { } from)
        {
            DragLine.Data = NodeLinkGeometry.Curve(from.Out, e.GetPosition(Surface), key: false);
        }
    }

    private void OnSurfaceUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragFrom is not { } from)
        {
            return;
        }

        NodeViewModel? to = NodeAt(e.GetPosition(Surface));
        bool key = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        EndDrag();
        if (to is { IsInput: false } && Panel is { } panel)
        {
            _ = panel.Connect(from.Id, to.Id, key);
        }

        e.Handled = true;
    }

    private void OnSurfaceLostCapture(object sender, MouseEventArgs e)
    {
        _dragFrom = null;
        DragLine.Data = null;
    }

    private void EndDrag()
    {
        _dragFrom = null;
        DragLine.Data = null;
        if (Surface.IsMouseCaptured)
        {
            Surface.ReleaseMouseCapture();
        }
    }

    /// <summary>The node whose box is under a point of the surface, if any.</summary>
    private NodeViewModel? NodeAt(Point point) =>
        Panel?.Nodes.FirstOrDefault(node => new Rect(node.X, node.Y, NodeViewModel.Width, NodeViewModel.Height).Contains(point));
}
