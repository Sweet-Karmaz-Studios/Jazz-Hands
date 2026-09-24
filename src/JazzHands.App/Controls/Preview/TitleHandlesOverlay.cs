using System.Numerics;
using System.Windows;
using System.Windows.Media;

namespace JazzHands.App.Controls.Preview;

/// <summary>
/// The selected title's frame on the preview: its box as it lies on the picture, a grip at each
/// corner for scaling, and a handle above for turning.
/// </summary>
/// <remarks>
/// Drawn in WPF over the picture like the guides, so it never reaches a frame or an export. It
/// takes no mouse input itself: the preview's view hit-tests through
/// <see cref="ViewModels.Playback.TitleHandlesViewModel"/>, which knows what a drag does.
/// </remarks>
public sealed class TitleHandlesOverlay : FrameworkElement
{
    /// <summary>The box's corners, clockwise from the top left, in sequence pixels from the frame centre.</summary>
    public static readonly DependencyProperty CornersProperty = DependencyProperty.Register(
        nameof(Corners),
        typeof(IReadOnlyList<Vector2>),
        typeof(TitleHandlesOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Where the turning handle is, in sequence pixels from the frame centre.</summary>
    public static readonly DependencyProperty RotateHandleProperty = DependencyProperty.Register(
        nameof(RotateHandle),
        typeof(Vector2),
        typeof(TitleHandlesOverlay),
        new FrameworkPropertyMetadata(Vector2.Zero, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Where the picture is, in this element's coordinates.</summary>
    public static readonly DependencyProperty PictureProperty = DependencyProperty.Register(
        nameof(Picture),
        typeof(Rect),
        typeof(TitleHandlesOverlay),
        new FrameworkPropertyMetadata(Rect.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The sequence's size, which the picture shows.</summary>
    public static readonly DependencyProperty SequenceSizeProperty = DependencyProperty.Register(
        nameof(SequenceSize),
        typeof(Size),
        typeof(TitleHandlesOverlay),
        new FrameworkPropertyMetadata(new Size(1920, 1080), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The frame's colour.</summary>
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush),
        typeof(Brush),
        typeof(TitleHandlesOverlay),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Half a grip's side, in this element's units.</summary>
    public const double Grip = 4.5;

    /// <summary>Creates the overlay.</summary>
    public TitleHandlesOverlay()
    {
        IsHitTestVisible = false;
    }

    /// <summary>The box's corners.</summary>
    public IReadOnlyList<Vector2>? Corners
    {
        get => (IReadOnlyList<Vector2>?)GetValue(CornersProperty);
        set => SetValue(CornersProperty, value);
    }

    /// <summary>Where the turning handle is.</summary>
    public Vector2 RotateHandle
    {
        get => (Vector2)GetValue(RotateHandleProperty);
        set => SetValue(RotateHandleProperty, value);
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

    /// <summary>The frame's colour.</summary>
    public Brush Brush
    {
        get => (Brush)GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    /// <summary>A place on the sequence, from the frame centre, as a point on this element.</summary>
    public static Point ToScreen(Vector2 at, Rect picture, Size sequence) => new(
        picture.X + (picture.Width * (0.5 + (at.X / sequence.Width))),
        picture.Y + (picture.Height * (0.5 + (at.Y / sequence.Height))));

    /// <summary>A point on this element as a place on the sequence, off the picture as well as on it.</summary>
    public static Vector2 ToSequence(Point point, Rect picture, Size sequence) => new(
        (float)((((point.X - picture.X) / picture.Width) - 0.5) * sequence.Width),
        (float)((((point.Y - picture.Y) / picture.Height) - 0.5) * sequence.Height));

    /// <summary>The corners' bounding box on this element, for the text editor to sit over.</summary>
    public static Rect Bounds(IReadOnlyList<Vector2> corners, Rect picture, Size sequence)
    {
        ArgumentNullException.ThrowIfNull(corners);
        Point[] points = [.. corners.Select(corner => ToScreen(corner, picture, sequence))];
        double left = points.Min(point => point.X);
        double top = points.Min(point => point.Y);
        return new Rect(left, top, points.Max(point => point.X) - left, points.Max(point => point.Y) - top);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        Rect picture = Picture;
        Size sequence = SequenceSize;
        if (Corners is not { Count: 4 } corners || picture.IsEmpty || sequence.Width <= 0 || sequence.Height <= 0)
        {
            return;
        }

        Point[] points = [.. corners.Select(corner => ToScreen(corner, picture, sequence))];
        var line = new Pen(Brush, 1.0);
        var shade = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 3.0);
        line.Freeze();
        shade.Freeze();

        var outline = new StreamGeometry();
        using (StreamGeometryContext context = outline.Open())
        {
            context.BeginFigure(points[0], isFilled: false, isClosed: true);
            context.PolyLineTo(points[1..], isStroked: true, isSmoothJoin: false);
        }

        outline.Freeze();

        // A dark line under the coloured one, so the frame shows over white text and dark footage alike.
        drawingContext.DrawGeometry(null, shade, outline);
        drawingContext.DrawGeometry(null, line, outline);

        Point handle = ToScreen(RotateHandle, picture, sequence);
        Point top = new((points[0].X + points[1].X) / 2.0, (points[0].Y + points[1].Y) / 2.0);
        drawingContext.DrawLine(line, top, handle);
        drawingContext.DrawEllipse(Brushes.White, line, handle, Grip, Grip);

        foreach (Point corner in points)
        {
            drawingContext.DrawRectangle(Brushes.White, line, new Rect(corner.X - Grip, corner.Y - Grip, Grip * 2.0, Grip * 2.0));
        }
    }
}
