using System.Numerics;
using Vector = System.Windows.Vector;
using System.Windows;
using System.Windows.Media;
using JazzHands.App.ViewModels.Playback;

namespace JazzHands.App.Controls.Preview;

/// <summary>
/// The selected 3D clip's handle on the preview (Phase 49a): red, green and blue arrows along the
/// world's X, Y and Z as the camera sees them, a square at the pivot, a ring round it and two
/// knobs. Drawn in WPF over the picture, so it never reaches a frame; it takes no input itself,
/// the preview's view hit-tests through <see cref="Gizmo3DViewModel"/>.
/// </summary>
public sealed class Gizmo3DOverlay : FrameworkElement
{
    /// <summary>The handle to draw.</summary>
    public static readonly DependencyProperty GizmoProperty = DependencyProperty.Register(
        nameof(Gizmo),
        typeof(Gizmo3DViewModel),
        typeof(Gizmo3DOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnGizmoChanged));

    /// <summary>Where the picture is, in this element's coordinates.</summary>
    public static readonly DependencyProperty PictureProperty = DependencyProperty.Register(
        nameof(Picture),
        typeof(Rect),
        typeof(Gizmo3DOverlay),
        new FrameworkPropertyMetadata(Rect.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The sequence's size, which the picture shows.</summary>
    public static readonly DependencyProperty SequenceSizeProperty = DependencyProperty.Register(
        nameof(SequenceSize),
        typeof(Size),
        typeof(Gizmo3DOverlay),
        new FrameworkPropertyMetadata(new Size(1920, 1080), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The knobs' outline colour.</summary>
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush),
        typeof(Brush),
        typeof(Gizmo3DOverlay),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush XBrush = Frozen(Color.FromRgb(0xE5, 0x48, 0x48));
    private static readonly Brush YBrush = Frozen(Color.FromRgb(0x4C, 0xC2, 0x5A));
    private static readonly Brush ZBrush = Frozen(Color.FromRgb(0x4A, 0x8C, 0xF0));
    private static readonly Pen Shade = FrozenPen(Frozen(Color.FromArgb(0x80, 0, 0, 0)), 3.5);

    /// <summary>Creates the overlay.</summary>
    public Gizmo3DOverlay()
    {
        IsHitTestVisible = false;
    }

    /// <summary>The handle to draw.</summary>
    public Gizmo3DViewModel? Gizmo
    {
        get => (Gizmo3DViewModel?)GetValue(GizmoProperty);
        set => SetValue(GizmoProperty, value);
    }

    /// <summary>Where the picture is.</summary>
    public Rect Picture
    {
        get => (Rect)GetValue(PictureProperty);
        set => SetValue(PictureProperty, value);
    }

    /// <summary>The sequence's size.</summary>
    public Size SequenceSize
    {
        get => (Size)GetValue(SequenceSizeProperty);
        set => SetValue(SequenceSizeProperty, value);
    }

    /// <summary>The knobs' outline colour.</summary>
    public Brush Brush
    {
        get => (Brush)GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        Rect picture = Picture;
        Size sequence = SequenceSize;
        if (Gizmo is not { IsShown: true } gizmo || picture.IsEmpty || sequence.Width <= 0 || sequence.Height <= 0)
        {
            return;
        }

        Point Screen(Vector2 at) => TitleHandlesOverlay.ToScreen(at, picture, sequence);
        Point pivot = Screen(gizmo.Pivot);
        double scale = picture.Width / sequence.Width;
        double grip = TitleHandlesOverlay.Grip;

        // The ring first, under the arrows; none for what only moves (a light).
        // Light over its dark shade, like the knobs, so it shows over blue footage as well as dark.
        Pen ring = FrozenPen(Frozen(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), 1.25);
        double radius = gizmo.RingRadius * scale;
        if (gizmo.Turns)
        {
            drawingContext.DrawEllipse(null, Shade, pivot, radius, radius);
            drawingContext.DrawEllipse(null, ring, pivot, radius, radius);
        }

        // Z under X and Y, since it is the one that most often points at the viewer.
        Arrow(drawingContext, pivot, Screen(gizmo.ZEnd), ZBrush);
        Arrow(drawingContext, pivot, Screen(gizmo.XEnd), XBrush);
        Arrow(drawingContext, pivot, Screen(gizmo.YEnd), YBrush);

        foreach (Vector2 knob in gizmo.Turns ? new[] { gizmo.TurnXKnob, gizmo.TurnYKnob } : [])
        {
            Point at = Screen(knob);
            drawingContext.DrawEllipse(Brushes.White, FrozenPen(Brush, 1.25), at, grip, grip);
        }

        drawingContext.DrawRectangle(Brushes.White, FrozenPen(Brushes.Black, 1.0), new Rect(pivot.X - grip, pivot.Y - grip, grip * 2.0, grip * 2.0));
    }

    private static void OnGizmoChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        var overlay = (Gizmo3DOverlay)owner;
        if (e.OldValue is Gizmo3DViewModel old)
        {
            old.PropertyChanged -= overlay.OnGizmoPropertyChanged;
        }

        if (e.NewValue is Gizmo3DViewModel added)
        {
            added.PropertyChanged += overlay.OnGizmoPropertyChanged;
        }
    }

    private void OnGizmoPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => InvalidateVisual();

    /// <summary>A line from the pivot with a head at its tip.</summary>
    private static void Arrow(DrawingContext context, Point from, Point to, Brush brush)
    {
        Vector along = to - from;
        if (along.Length < 1.0)
        {
            return;
        }

        Pen pen = FrozenPen(brush, 2.0);
        context.DrawLine(Shade, from, to);
        context.DrawLine(pen, from, to);

        along.Normalize();
        var side = new Vector(-along.Y, along.X);
        var head = new StreamGeometry();
        using (StreamGeometryContext figure = head.Open())
        {
            figure.BeginFigure(to + (along * 4.0), isFilled: true, isClosed: true);
            figure.LineTo(to - (along * 7.0) + (side * 5.0), isStroked: true, isSmoothJoin: false);
            figure.LineTo(to - (along * 7.0) - (side * 5.0), isStroked: true, isSmoothJoin: false);
        }

        head.Freeze();
        context.DrawGeometry(brush, Shade, head);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
