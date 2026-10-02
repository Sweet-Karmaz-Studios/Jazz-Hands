using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using JazzHands.App.ViewModels.Comp;

namespace JazzHands.App.Views.Comp;

/// <summary>
/// The Nodes panel (Phase 49): draws the view model's nodes and wires, and turns gestures into its
/// commands. A node dragged by its title moves locally and is sent once when the button comes up;
/// a wire dragged from a node's output dot is sent when it lands on a port or a node.
/// </summary>
public partial class CompPanelView : UserControl
{
    private CompNodeViewModel? _moving;
    private Point _grab;
    private Point _start;
    private CompNodeViewModel? _wiring;

    /// <summary>Creates the view.</summary>
    public CompPanelView() => InitializeComponent();

    private CompPanelViewModel? Panel => DataContext as CompPanelViewModel;

    private void OnNodeDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CompNodeViewModel node } && Panel is { } panel)
        {
            panel.Select(node);
            _moving = node;
            _grab = e.GetPosition(Surface);
            _start = new Point(node.X, node.Y);
            Surface.CaptureMouse();
            Focus();
            e.Handled = true;
        }
    }

    private void OnOutputDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CompNodeViewModel node })
        {
            _wiring = node;
            DragLine.Data = WireGeometry.Curve(node.Output, e.GetPosition(Surface));
            Surface.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnSurfaceMove(object sender, MouseEventArgs e)
    {
        Point at = e.GetPosition(Surface);
        if (_moving is { } node)
        {
            Vector moved = at - _grab;
            node.X = Math.Max(0, _start.X + moved.X);
            node.Y = Math.Max(0, _start.Y + moved.Y);
        }
        else if (_wiring is { } from)
        {
            DragLine.Data = WireGeometry.Curve(from.Output, at);
        }
    }

    private void OnSurfaceUp(object sender, MouseButtonEventArgs e)
    {
        Point at = e.GetPosition(Surface);
        CompPanelViewModel? panel = Panel;
        if (_moving is { } node && panel is not null)
        {
            _moving = null;
            Surface.ReleaseMouseCapture();
            if (Math.Abs(node.X - _start.X) > 0.5 || Math.Abs(node.Y - _start.Y) > 0.5)
            {
                _ = panel.MoveAsync(node, panel.ToGraph(new Point(node.X, node.Y)));
            }

            return;
        }

        if (_wiring is { } from && panel is not null)
        {
            _wiring = null;
            DragLine.Data = null;
            Surface.ReleaseMouseCapture();

            // Onto a port, or onto a node for its first free port.
            if (panel.PortAt(at) is { } port && port.NodeId != from.Id)
            {
                _ = panel.ConnectAsync(from.Id, port.NodeId, port.Name);
            }
            else if (panel.NodeAt(at) is { } to && to.Id != from.Id)
            {
                _ = panel.ConnectAsync(from.Id, to.Id, null);
            }
        }
    }

    private void OnSurfaceRightUp(object sender, MouseButtonEventArgs e)
    {
        if (Panel is not { HasGraph: true } panel)
        {
            return;
        }

        Point at = e.GetPosition(Surface);
        if (panel.PortAt(at) is { IsWired: true } port)
        {
            _ = panel.DisconnectAsync(port.NodeId, port.Name);
            e.Handled = true;
            return;
        }

        if (panel.NodeAt(at) is null)
        {
            ShowAddMenu(Surface, panel.ToGraph(at));
            e.Handled = true;
        }
    }

    private void OnLostCapture(object sender, MouseEventArgs e)
    {
        if (_moving is { } node)
        {
            node.X = _start.X;
            node.Y = _start.Y;
        }

        _moving = null;
        _wiring = null;
        DragLine.Data = null;
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (Panel is { HasGraph: true })
        {
            ShowAddMenu(AddButton, at: null);
        }
    }

    /// <summary>The node types grouped into submenus: at a place in the graph, or wired from the selected node.</summary>
    private void ShowAddMenu(UIElement target, Point? at)
    {
        if (Panel is not { } panel)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = target };
        foreach (IGrouping<string, CompNodeType> group in panel.NodeTypes.GroupBy(type => type.Group))
        {
            var parent = new MenuItem { Header = group.Key };
            foreach (CompNodeType type in group)
            {
                var item = new MenuItem { Header = type.Name, Tag = type.TypeId };
                item.Click += (_, _) =>
                {
                    _ = at is { } place ? panel.AddAsync(type.TypeId, place) : panel.AddTypeCommand.ExecuteAsync(type.TypeId);
                };
                parent.Items.Add(item);
            }

            menu.Items.Add(parent);
        }

        menu.IsOpen = true;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Panel is not { } panel)
        {
            return;
        }

        panel.Zoom = Math.Clamp(panel.Zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.3, 2.5);
        e.Handled = true;
    }
}

/// <summary>A wire's curve: leaving a node's right side and arriving at a port from the left.</summary>
public sealed class WireGeometry : IValueConverter
{
    /// <summary>The one instance.</summary>
    public static WireGeometry Instance { get; } = new();

    /// <summary>A curve from one point to another, leaving rightwards and arriving rightwards.</summary>
    public static Geometry Curve(Point start, Point end)
    {
        double reach = Math.Max(30, Math.Abs(end.X - start.X) / 2);
        var figure = new PathFigure { StartPoint = start, IsFilled = false };
        figure.Segments.Add(new BezierSegment(new Point(start.X + reach, start.Y), new Point(end.X - reach, end.Y), end, isStroked: true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is CompWireViewModel wire ? Curve(wire.Start, wire.End) : Geometry.Empty;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True for false, for enabling what only makes sense when something is not so.</summary>
public sealed class Not : IValueConverter
{
    /// <summary>The one instance.</summary>
    public static Not Instance { get; } = new();

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
