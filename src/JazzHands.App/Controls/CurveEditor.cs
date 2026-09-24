using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using JazzHands.Core.Effects;

namespace JazzHands.App.Controls;

/// <summary>
/// A curve drawn and edited by its points, for the color.curves parameters.
/// </summary>
/// <remarks>
/// <see cref="Points"/> is the parameter's own text, <c>"0,0 0.25,0.2 1,1"</c>, so what the
/// editor shows is exactly what the renderer reads, drawn through <see cref="CurvePoints"/>.
/// Click empty space to add a point; drag a point to move it, between its neighbours; double-click
/// a point to take it away. A tone curve keeps a point at each end, which moves only up and down; a
/// hue curve wraps and has no ends. Backspace or Delete with no point held puts the curve back to
/// <see cref="Neutral"/>. <see cref="IsDragging"/> is true while a point is held.
/// </remarks>
public sealed class CurveEditor : FrameworkElement
{
    /// <summary>The curve as text.</summary>
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points),
        typeof(string),
        typeof(CurveEditor),
        new FrameworkPropertyMetadata("0,0 1,1", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>True for a hue curve, whose ends wrap.</summary>
    public static readonly DependencyProperty IsPeriodicProperty = DependencyProperty.Register(
        nameof(IsPeriodic),
        typeof(bool),
        typeof(CurveEditor),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The text for no change: y = x, or flat at a half.</summary>
    public static readonly DependencyProperty NeutralProperty = DependencyProperty.Register(
        nameof(Neutral),
        typeof(string),
        typeof(CurveEditor),
        new FrameworkPropertyMetadata("0,0 1,1", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The curve's colour.</summary>
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(CurveEditor),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>True while a point is held.</summary>
    public static readonly DependencyProperty IsDraggingProperty = DependencyProperty.Register(
        nameof(IsDragging),
        typeof(bool),
        typeof(CurveEditor),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private const double Grab = 7;
    private int _held = -1;

    /// <summary>Creates an editor.</summary>
    public CurveEditor()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Cross;
    }

    /// <summary>The curve as text.</summary>
    public string Points
    {
        get => (string)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>True for a hue curve.</summary>
    public bool IsPeriodic
    {
        get => (bool)GetValue(IsPeriodicProperty);
        set => SetValue(IsPeriodicProperty, value);
    }

    /// <summary>The text for no change.</summary>
    public string Neutral
    {
        get => (string)GetValue(NeutralProperty);
        set => SetValue(NeutralProperty, value);
    }

    /// <summary>The curve's colour.</summary>
    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>True while a point is held.</summary>
    public bool IsDragging
    {
        get => (bool)GetValue(IsDraggingProperty);
        set => SetValue(IsDraggingProperty, value);
    }

    private CurvePoints Curve => CurvePoints.Parse(Points, CurvePoints.Parse(Neutral), IsPeriodic);

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 256 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 256 : availableSize.Height);

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        Rect box = Box();
        var ground = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
        ground.Freeze();
        drawingContext.DrawRectangle(ground, null, box);

        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1);
        grid.Freeze();
        for (int line = 1; line < 4; line++)
        {
            double x = box.Left + (box.Width * line / 4);
            double y = box.Top + (box.Height * line / 4);
            drawingContext.DrawLine(grid, new Point(x, box.Top), new Point(x, box.Bottom));
            drawingContext.DrawLine(grid, new Point(box.Left, y), new Point(box.Right, y));
        }

        // Where no change would be: the diagonal, or the flat middle of a hue curve.
        var neutral = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = DashStyles.Dash };
        neutral.Freeze();
        CurvePoints resting = CurvePoints.Parse(Neutral);
        drawingContext.DrawLine(neutral, ToScreen(new Vector2(0, resting.Evaluate(0)), box), ToScreen(new Vector2(1, resting.Evaluate(1)), box));

        CurvePoints curve = Curve;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            const int Steps = 128;
            for (int step = 0; step <= Steps; step++)
            {
                float x = step / (float)Steps;
                Point at = ToScreen(new Vector2(x, curve.Evaluate(x)), box);
                if (step == 0)
                {
                    context.BeginFigure(at, false, false);
                }
                else
                {
                    context.LineTo(at, true, false);
                }
            }
        }

        geometry.Freeze();
        var stroke = new Pen(Stroke, 1.5);
        stroke.Freeze();
        drawingContext.DrawGeometry(null, stroke, geometry);

        var handle = new Pen(Stroke, 1.5);
        handle.Freeze();
        for (int index = 0; index < curve.Points.Length; index++)
        {
            Point at = ToScreen(curve.Points[index], box);
            drawingContext.DrawEllipse(index == _held ? Stroke : ground, handle, at, 4, 4);
        }
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonDown(e);
        Focus();

        Rect box = Box();
        Point at = e.GetPosition(this);
        List<Vector2> points = [.. Curve.Points];
        int near = Nearest(points, at, box);

        if (e.ClickCount == 2)
        {
            if (near >= 0 && !IsEnd(points, near))
            {
                points.RemoveAt(near);
                Write(points);
            }

            e.Handled = true;
            return;
        }

        if (near < 0)
        {
            Vector2 added = FromScreen(at, box);
            points.Add(added);
            points.Sort((a, b) => a.X.CompareTo(b.X));
            near = points.IndexOf(added);
            Write(points);
        }

        _held = near;
        IsDragging = true;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        if (_held < 0)
        {
            return;
        }

        Rect box = Box();
        List<Vector2> points = [.. Curve.Points];
        if (_held >= points.Count)
        {
            return;
        }

        Vector2 moved = FromScreen(e.GetPosition(this), box);

        // Between its neighbours, so points never swap order; the ends of a tone curve stay at
        // their x and move only up and down.
        const float Gap = 0.01f;
        float low = _held > 0 ? points[_held - 1].X + Gap : 0;
        float high = _held < points.Count - 1 ? points[_held + 1].X - Gap : 1;
        float x = IsEnd(points, _held) ? points[_held].X : Math.Clamp(moved.X, low, Math.Max(low, high));
        points[_held] = new Vector2(x, moved.Y);
        Write(points);
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonUp(e);
        if (_held >= 0)
        {
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _held = -1;
        IsDragging = false;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if (e.Key is Key.Back or Key.Delete && _held < 0)
        {
            Points = Neutral;
            e.Handled = true;
        }
    }

    /// <summary>The square the curve is drawn in, inside a small margin so end points can be grabbed.</summary>
    private Rect Box()
    {
        const double Margin = 6;
        return new Rect(Margin, Margin, Math.Max(1, ActualWidth - (2 * Margin)), Math.Max(1, ActualHeight - (2 * Margin)));
    }

    private static Point ToScreen(Vector2 point, Rect box) => new(box.Left + (point.X * box.Width), box.Bottom - (point.Y * box.Height));

    private static Vector2 FromScreen(Point point, Rect box) => new(
        (float)Math.Clamp((point.X - box.Left) / box.Width, 0, 1),
        (float)Math.Clamp((box.Bottom - point.Y) / box.Height, 0, 1));

    private int Nearest(List<Vector2> points, Point at, Rect box)
    {
        int best = -1;
        double closest = Grab * Grab;
        for (int index = 0; index < points.Count; index++)
        {
            System.Windows.Vector distance = ToScreen(points[index], box) - at;
            if (distance.LengthSquared <= closest)
            {
                closest = distance.LengthSquared;
                best = index;
            }
        }

        return best;
    }

    /// <summary>True for the first or last point of a tone curve, which keeps its x.</summary>
    private bool IsEnd(List<Vector2> points, int index) => !IsPeriodic && (index == 0 || index == points.Count - 1);

    private void Write(List<Vector2> points)
    {
        Points = CurvePoints.Format(points);
        InvalidateVisual();
    }
}
