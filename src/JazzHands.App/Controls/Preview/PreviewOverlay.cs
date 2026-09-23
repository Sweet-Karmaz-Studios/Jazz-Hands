using System.Windows;
using System.Windows.Media;

namespace JazzHands.App.Controls.Preview;

/// <summary>
/// Guides drawn over the picture: the action and title safe areas, and a rule of thirds grid.
/// </summary>
/// <remarks>
/// Drawn in WPF over the D3DImage rather than into the frame, so they never reach an export and
/// cost nothing when they are off. That only works on the D3DImage path; the full screen swap
/// chain is a child window that WPF cannot draw over, which is fine because nobody wants guides
/// on the client monitor.
///
/// The safe areas are the ones SMPTE ST 2046-1 gives for 16:9: action safe at 93% of each
/// dimension and title safe at 90%.
/// </remarks>
public sealed class PreviewOverlay : FrameworkElement
{
    /// <summary>Where the picture is, in this element's coordinates.</summary>
    public static readonly DependencyProperty PictureProperty = DependencyProperty.Register(
        nameof(Picture),
        typeof(Rect),
        typeof(PreviewOverlay),
        new FrameworkPropertyMetadata(Rect.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Whether to draw the safe areas.</summary>
    public static readonly DependencyProperty ShowSafeAreasProperty = DependencyProperty.Register(
        nameof(ShowSafeAreas),
        typeof(bool),
        typeof(PreviewOverlay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Whether to draw the thirds.</summary>
    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid),
        typeof(bool),
        typeof(PreviewOverlay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The fraction of each dimension inside action safe.</summary>
    public const double ActionSafe = 0.93;

    /// <summary>The fraction of each dimension inside title safe.</summary>
    public const double TitleSafe = 0.90;

    /// <summary>The colour of the grid and the action safe area.</summary>
    public static readonly DependencyProperty GuideBrushProperty = DependencyProperty.Register(
        nameof(GuideBrush),
        typeof(Brush),
        typeof(PreviewOverlay),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The colour of the title safe area, which is set apart from action safe.</summary>
    public static readonly DependencyProperty TitleBrushProperty = DependencyProperty.Register(
        nameof(TitleBrush),
        typeof(Brush),
        typeof(PreviewOverlay),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Creates the overlay. It takes no mouse input; clicks go to the picture beneath.</summary>
    public PreviewOverlay()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    /// <summary>Where the picture is, in this element's coordinates.</summary>
    public Rect Picture
    {
        get => (Rect)GetValue(PictureProperty);
        set => SetValue(PictureProperty, value);
    }

    /// <summary>Whether to draw the safe areas.</summary>
    public bool ShowSafeAreas
    {
        get => (bool)GetValue(ShowSafeAreasProperty);
        set => SetValue(ShowSafeAreasProperty, value);
    }

    /// <summary>Whether to draw the thirds.</summary>
    public bool ShowGrid
    {
        get => (bool)GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    /// <summary>The colour of the grid and the action safe area.</summary>
    public Brush GuideBrush
    {
        get => (Brush)GetValue(GuideBrushProperty);
        set => SetValue(GuideBrushProperty, value);
    }

    /// <summary>The colour of the title safe area.</summary>
    public Brush TitleBrush
    {
        get => (Brush)GetValue(TitleBrushProperty);
        set => SetValue(TitleBrushProperty, value);
    }

    /// <summary>A rectangle shrunk about its centre to a fraction of its size.</summary>
    public static Rect Inset(Rect picture, double fraction)
    {
        double width = picture.Width * fraction;
        double height = picture.Height * fraction;
        return new Rect(
            picture.X + ((picture.Width - width) / 2.0),
            picture.Y + ((picture.Height - height) / 2.0),
            width,
            height);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        Rect picture = Picture;
        if (picture.IsEmpty || picture.Width < 4 || picture.Height < 4)
        {
            return;
        }

        var guide = new Pen(GuideBrush, 1.0);
        var title = new Pen(TitleBrush, 1.0);

        drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize)));

        if (ShowGrid)
        {
            for (int line = 1; line < 3; line++)
            {
                double x = picture.X + (picture.Width * line / 3.0);
                double y = picture.Y + (picture.Height * line / 3.0);
                drawingContext.DrawLine(guide, new Point(x, picture.Top), new Point(x, picture.Bottom));
                drawingContext.DrawLine(guide, new Point(picture.Left, y), new Point(picture.Right, y));
            }
        }

        if (ShowSafeAreas)
        {
            drawingContext.DrawRectangle(null, guide, Inset(picture, ActionSafe));
            drawingContext.DrawRectangle(null, title, Inset(picture, TitleSafe));

            // A small cross at the centre, which is what a title is lined up on.
            Point centre = new(picture.X + (picture.Width / 2.0), picture.Y + (picture.Height / 2.0));
            drawingContext.DrawLine(guide, centre with { X = centre.X - 8 }, centre with { X = centre.X + 8 });
            drawingContext.DrawLine(guide, centre with { Y = centre.Y - 8 }, centre with { Y = centre.Y + 8 });
        }

        drawingContext.Pop();
    }
}
