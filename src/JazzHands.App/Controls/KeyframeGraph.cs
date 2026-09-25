using System.Globalization;
using Vector2 = System.Numerics.Vector2;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using JazzHands.App.ViewModels.Curves;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.App.Controls;

/// <summary>
/// The Curve Editor's graph: every shown channel's curve over the clip, its keyframes, the bezier
/// handles of the selected ones, a value scale and a time ruler.
/// </summary>
/// <remarks>
/// Click a keyframe to select it (Shift or Ctrl adds), drag empty space to box select, drag the
/// selection to move it in time and value (Shift keeps to the axis it started along), drag a handle
/// to shape a bezier. Delete removes, Ctrl+C and Ctrl+V copy and paste at the playhead, Esc lets
/// go. What any of it changes goes through <see cref="CurveEditorPanelViewModel"/>.
/// </remarks>
public sealed class KeyframeGraph : FrameworkElement
{
    /// <summary>The panel whose curves it draws.</summary>
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model),
        typeof(CurveEditorPanelViewModel),
        typeof(KeyframeGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Bound to the panel's revision, so any change redraws.</summary>
    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision),
        typeof(int),
        typeof(KeyframeGraph),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Left = 52;
    private const double Bottom = 20;
    private const double Top = 8;
    private const double Right = 8;
    private const double Reach = 6;

    private Point? _pressed;
    private Point _now;
    private Press _press;
    private KeyRef _handleKey;
    private bool _handleOut;

    /// <summary>Creates the graph.</summary>
    public KeyframeGraph()
    {
        Focusable = true;
        ClipToBounds = true;
        FocusVisualStyle = null;
    }

    private enum Press
    {
        None,
        Box,
        Keys,
        Handle,
    }

    /// <summary>The panel whose curves it draws.</summary>
    public CurveEditorPanelViewModel? Model
    {
        get => (CurveEditorPanelViewModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>Bound to the panel's revision.</summary>
    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    private Rect Plot => new(Left, Top, Math.Max(1, ActualWidth - Left - Right), Math.Max(1, ActualHeight - Top - Bottom));

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        drawingContext.DrawRectangle(Brush("Brush.Background.Panel", Brushes.Black), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Model is not { } model || model.Length <= Flicks.Zero)
        {
            return;
        }

        Rect plot = Plot;
        (double min, double max) = model.Range();
        Brush text = Brush("Brush.Text.Secondary", Brushes.Gray);
        var grid = new Pen(Brush("Brush.Border", Brushes.DimGray), 1);
        grid.Freeze();
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Five value lines with their numbers, and a tick each second (or each frame when close).
        for (int line = 0; line <= 4; line++)
        {
            double value = min + ((max - min) * line / 4);
            double y = Y(value, plot, min, max);
            drawingContext.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            drawingContext.DrawText(Label(Format(value, model.IsNormalized), text, dpi), new Point(2, y - 7));
        }

        double seconds = model.Length.ToSeconds();
        double step = seconds <= 2 ? 0.25 : seconds <= 10 ? 1 : seconds <= 60 ? 5 : 30;
        for (double at = 0; at <= seconds + 1e-9; at += step)
        {
            double x = X(Flicks.FromSeconds(at), plot, model.Length);
            drawingContext.DrawLine(grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            drawingContext.DrawText(Label(Timecode.FormatClock(Flicks.FromSeconds(at)), text, dpi), new Point(x + 2, plot.Bottom + 3));
        }

        for (int index = 0; index < model.Channels.Count; index++)
        {
            CurveChannel channel = model.Channels[index];
            if (channel.IsShown)
            {
                DrawChannel(drawingContext, model, channel, index, plot, min, max);
            }
        }

        if (_press == Press.Box && _pressed is { } from)
        {
            var box = new Rect(from, _now);
            drawingContext.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x30, 0x4F, 0x8F, 0xE8)), new Pen(Brushes.CornflowerBlue, 1), box);
        }
    }

    /// <inheritdoc />
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseDown(e);
        Focus();
        if (e.ChangedButton != MouseButton.Left || Model is not { } model)
        {
            return;
        }

        Point at = e.GetPosition(this);
        bool add = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        _pressed = at;
        _now = at;

        if (HandleAt(model, at) is { } handle)
        {
            (_handleKey, _handleOut) = handle;
            _press = Press.Handle;
        }
        else if (KeyAt(model, at) is { } key)
        {
            if (!model.Selected.Contains(key))
            {
                model.Select(key, add);
            }

            _press = Press.Keys;
        }
        else
        {
            if (!add)
            {
                model.ClearSelection();
            }

            _press = Press.Box;
        }

        CaptureMouse();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        if (_pressed is not { } from || Model is not { } model)
        {
            return;
        }

        _now = e.GetPosition(this);
        Rect plot = Plot;
        switch (_press)
        {
            case Press.Keys:
            {
                Vector delta = _now - from;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    delta = Math.Abs(delta.X) >= Math.Abs(delta.Y) ? new Vector(delta.X, 0) : new Vector(0, delta.Y);
                }

                (double min, double max) = model.Range();
                var time = new Flicks((long)(delta.X / plot.Width * model.Length.Value));
                double value = -delta.Y / plot.Height * (max - min);
                model.DragBy(time, value);
                break;
            }

            default:
                InvalidateVisual();
                break;
        }
    }

    /// <inheritdoc />
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseUp(e);
        if (_pressed is not { } from || Model is not { } model || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        Point to = e.GetPosition(this);
        Rect plot = Plot;
        (double min, double max) = model.Range();
        ReleaseMouseCapture();
        _pressed = null;

        switch (_press)
        {
            case Press.Keys when (to - from).Length < 3:
                model.CancelDrag();
                break;

            case Press.Keys:
                _ = model.CommitDragAsync();
                break;

            case Press.Handle:
            {
                CurveChannel channel = model.Channels[_handleKey.Channel];
                Flicks time = Time(to.X, plot, model.Length);
                double graph = min + ((plot.Bottom - to.Y) / plot.Height * (max - min));
                double value = model.IsNormalized ? Denormalize(model, channel, graph) : graph;
                _ = model.SetHandleAsync(_handleKey, _handleOut, time, value);
                break;
            }

            case Press.Box:
            {
                var box = new Rect(from, to);
                bool add = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                model.SelectWhere(key => box.Contains(KeyPoint(model, key, plot, min, max)), add);
                break;
            }
        }

        _press = Press.None;
        InvalidateVisual();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if (Model is not { } model)
        {
            return;
        }

        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Delete or Key.Back:
                _ = model.DeleteAsync();
                break;
            case Key.C when control:
                model.Copy();
                break;
            case Key.V when control:
                _ = model.PasteAsync();
                break;
            case Key.Escape:
                if (_pressed is not null)
                {
                    ReleaseMouseCapture();
                    _pressed = null;
                    _press = Press.None;
                    model.CancelDrag();
                }
                else
                {
                    model.ClearSelection();
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private static double X(Flicks time, Rect plot, Flicks length) => plot.Left + (plot.Width * time.Value / Math.Max(1.0, length.Value));

    private static double Y(double value, Rect plot, double min, double max) => plot.Bottom - (plot.Height * (value - min) / (max - min));

    private static Flicks Time(double x, Rect plot, Flicks length) => new((long)((x - plot.Left) / plot.Width * length.Value));

    private static double Denormalize(CurveEditorPanelViewModel model, CurveChannel channel, double graph)
    {
        (double low, double high) = model.Extent(channel);
        return low + (graph * (high - low));
    }

    private static string Format(double value, bool normalized) =>
        normalized ? value.ToString("0.00", CultureInfo.InvariantCulture)
        : Math.Abs(value) >= 100 ? value.ToString("0", CultureInfo.InvariantCulture)
        : value.ToString("0.##", CultureInfo.InvariantCulture);

    private Brush Brush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private FormattedText Label(string text, Brush brush, double dpi) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, brush, dpi);

    /// <summary>Where a keyframe is drawn, the drag included for a selected one.</summary>
    private static Point KeyPoint(CurveEditorPanelViewModel model, KeyRef key, Rect plot, double min, double max)
    {
        CurveChannel channel = model.Channels[key.Channel];
        CurveKey curveKey = channel.Keys[key.Index];
        bool dragged = model.IsDragging && model.Selected.Contains(key);
        Flicks time = curveKey.Time + (dragged ? model.DragTime : Flicks.Zero);
        double value = model.ToGraph(channel, curveKey.Value) + (dragged ? model.DragValue : 0);
        return new Point(X(time, plot, model.Length), Y(value, plot, min, max));
    }

    /// <summary>Where a keyframe's handle is drawn, from the segment it shapes.</summary>
    private static Point? HandlePoint(CurveEditorPanelViewModel model, KeyRef key, bool outgoing, Rect plot, double min, double max)
    {
        CurveChannel channel = model.Channels[key.Channel];
        int other = outgoing ? key.Index + 1 : key.Index - 1;
        if (other < 0 || other >= channel.Keys.Count)
        {
            return null;
        }

        CurveKey from = channel.Keys[outgoing ? key.Index : other];
        CurveKey to = channel.Keys[outgoing ? other : key.Index];
        if (from.Interp != Interp.Bezier)
        {
            return null;
        }

        Vector2 handle = outgoing ? from.OutHandle ?? new Vector2(1 / 3f, 0) : to.InHandle ?? new Vector2(2 / 3f, 1);
        Flicks time = from.Time + new Flicks((long)(handle.X * (to.Time - from.Time).Value));
        double value = model.ToGraph(channel, from.Value + (handle.Y * (to.Value - from.Value)));
        return new Point(X(time, plot, model.Length), Y(value, plot, min, max));
    }

    private KeyRef? KeyAt(CurveEditorPanelViewModel model, Point at)
    {
        Rect plot = Plot;
        (double min, double max) = model.Range();
        for (int channel = model.Channels.Count - 1; channel >= 0; channel--)
        {
            if (!model.Channels[channel].IsShown)
            {
                continue;
            }

            for (int index = 0; index < model.Channels[channel].Keys.Count; index++)
            {
                var key = new KeyRef(channel, index);
                if ((KeyPoint(model, key, plot, min, max) - at).Length <= Reach)
                {
                    return key;
                }
            }
        }

        return null;
    }

    private (KeyRef Key, bool Outgoing)? HandleAt(CurveEditorPanelViewModel model, Point at)
    {
        Rect plot = Plot;
        (double min, double max) = model.Range();
        foreach (KeyRef key in model.Selected)
        {
            foreach (bool outgoing in new[] { true, false })
            {
                if (HandlePoint(model, key, outgoing, plot, min, max) is { } point && (point - at).Length <= Reach)
                {
                    return (key, outgoing);
                }
            }
        }

        return null;
    }

    private void DrawChannel(DrawingContext drawingContext, CurveEditorPanelViewModel model, CurveChannel channel, int index, Rect plot, double min, double max)
    {
        var colour = (Color)ColorConverter.ConvertFromString(channel.Color);
        var pen = new Pen(new SolidColorBrush(colour), 1.5);
        pen.Freeze();

        var curve = new StreamGeometry();
        using (StreamGeometryContext context = curve.Open())
        {
            int steps = Math.Max(2, (int)plot.Width / 2);
            for (int step = 0; step <= steps; step++)
            {
                var time = new Flicks(model.Length.Value * step / steps);
                var point = new Point(X(time, plot, model.Length), Y(model.ToGraph(channel, channel.ValueAt(time)), plot, min, max));
                if (step == 0)
                {
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                }
                else
                {
                    context.LineTo(point, isStroked: true, isSmoothJoin: false);
                }
            }
        }

        curve.Freeze();
        drawingContext.DrawGeometry(null, pen, curve);

        var handlePen = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, colour.R, colour.G, colour.B)), 1);
        handlePen.Freeze();
        for (int key = 0; key < channel.Keys.Count; key++)
        {
            var reference = new KeyRef(index, key);
            Point at = KeyPoint(model, reference, plot, min, max);
            bool selected = model.Selected.Contains(reference);
            if (selected)
            {
                foreach (bool outgoing in new[] { true, false })
                {
                    if (HandlePoint(model, reference, outgoing, plot, min, max) is { } handle)
                    {
                        drawingContext.DrawLine(handlePen, at, handle);
                        drawingContext.DrawEllipse(Brushes.White, handlePen, handle, 3, 3);
                    }
                }
            }

            var diamond = new StreamGeometry();
            using (StreamGeometryContext context = diamond.Open())
            {
                context.BeginFigure(new Point(at.X, at.Y - 5), isFilled: true, isClosed: true);
                context.PolyLineTo([new Point(at.X + 5, at.Y), new Point(at.X, at.Y + 5), new Point(at.X - 5, at.Y)], isStroked: true, isSmoothJoin: false);
            }

            diamond.Freeze();
            drawingContext.DrawGeometry(selected ? Brushes.White : new SolidColorBrush(colour), pen, diamond);
        }
    }
}
