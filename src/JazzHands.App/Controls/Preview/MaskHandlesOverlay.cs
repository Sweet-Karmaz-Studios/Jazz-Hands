using System.Numerics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using JazzHands.App.ViewModels.Playback;

namespace JazzHands.App.Controls.Preview;

/// <summary>
/// The selected clip's masks on the preview: every outline, the active mask's points, tangent
/// handles and feather knob, and the shape being drawn.
/// </summary>
/// <remarks>
/// Drawn in WPF over the picture like the title frame, so it never reaches a frame or an export,
/// and takes no mouse input itself: the preview's view hit-tests through
/// <see cref="MaskHandlesViewModel"/>, which knows what a drag does.
/// </remarks>
public sealed class MaskHandlesOverlay : FrameworkElement
{
    /// <summary>The masks to draw.</summary>
    public static readonly DependencyProperty MasksProperty = DependencyProperty.Register(
        nameof(Masks),
        typeof(IReadOnlyList<MaskView>),
        typeof(MaskHandlesOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The shape being drawn: its points so far, or a box's two corners.</summary>
    public static readonly DependencyProperty SketchProperty = DependencyProperty.Register(
        nameof(Sketch),
        typeof(IReadOnlyList<Vector2>),
        typeof(MaskHandlesOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The drawing tool, which says how to draw the sketch.</summary>
    public static readonly DependencyProperty ToolProperty = DependencyProperty.Register(
        nameof(Tool),
        typeof(MaskTool),
        typeof(MaskHandlesOverlay),
        new FrameworkPropertyMetadata(MaskTool.Select, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Where the picture is, in this element's coordinates.</summary>
    public static readonly DependencyProperty PictureProperty = DependencyProperty.Register(
        nameof(Picture),
        typeof(Rect),
        typeof(MaskHandlesOverlay),
        new FrameworkPropertyMetadata(Rect.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The sequence's size, which the picture shows.</summary>
    public static readonly DependencyProperty SequenceSizeProperty = DependencyProperty.Register(
        nameof(SequenceSize),
        typeof(Size),
        typeof(MaskHandlesOverlay),
        new FrameworkPropertyMetadata(new Size(1920, 1080), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The active mask's colour.</summary>
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush),
        typeof(Brush),
        typeof(MaskHandlesOverlay),
        new FrameworkPropertyMetadata(Brushes.Gold, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The colour of an effect's masks, when not the one being edited.</summary>
    public static readonly DependencyProperty EffectBrushProperty = DependencyProperty.Register(
        nameof(EffectBrush),
        typeof(Brush),
        typeof(MaskHandlesOverlay),
        new FrameworkPropertyMetadata(Brushes.SkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Half a grip's side, in this element's units.</summary>
    public const double Grip = 4.0;

    /// <summary>Creates the overlay.</summary>
    public MaskHandlesOverlay()
    {
        IsHitTestVisible = false;
    }

    /// <summary>The masks to draw.</summary>
    public IReadOnlyList<MaskView>? Masks
    {
        get => (IReadOnlyList<MaskView>?)GetValue(MasksProperty);
        set => SetValue(MasksProperty, value);
    }

    /// <summary>The shape being drawn.</summary>
    public IReadOnlyList<Vector2>? Sketch
    {
        get => (IReadOnlyList<Vector2>?)GetValue(SketchProperty);
        set => SetValue(SketchProperty, value);
    }

    /// <summary>The drawing tool.</summary>
    public MaskTool Tool
    {
        get => (MaskTool)GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
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

    /// <summary>The active mask's colour.</summary>
    public Brush Brush
    {
        get => (Brush)GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    /// <summary>The colour of an effect's masks.</summary>
    public Brush EffectBrush
    {
        get => (Brush)GetValue(EffectBrushProperty);
        set => SetValue(EffectBrushProperty, value);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        Rect picture = Picture;
        Size sequence = SequenceSize;
        if (picture.IsEmpty || sequence.Width <= 0 || sequence.Height <= 0)
        {
            return;
        }

        Point At(Vector2 point) => TitleHandlesOverlay.ToScreen(point, picture, sequence);

        var active = new Pen(Brush, 1.0);
        var quiet = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), 1.0) { DashStyle = DashStyles.Dash };
        var effect = new Pen(EffectBrush, 1.0);
        var shade = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 3.0);
        active.Freeze();
        quiet.Freeze();
        effect.Freeze();
        shade.Freeze();
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (MaskView mask in Masks ?? [])
        {
            foreach (IReadOnlyList<Vector2> outline in mask.Outlines)
            {
                if (outline.Count < 2)
                {
                    continue;
                }

                var geometry = new StreamGeometry();
                using (StreamGeometryContext context = geometry.Open())
                {
                    context.BeginFigure(At(outline[0]), isFilled: false, isClosed: true);
                    context.PolyLineTo([.. outline.Skip(1).Select(At)], isStroked: true, isSmoothJoin: true);
                }

                geometry.Freeze();
                drawingContext.DrawGeometry(null, shade, geometry);
                drawingContext.DrawGeometry(null, mask.IsActive ? active : mask.EffectId is null ? quiet : effect, geometry);
            }

            // An effect's mask says whose it is, under its shape, so it is not taken for the clip's.
            if (mask.Label.Length > 0 && mask.Outlines.SelectMany(outline => outline).ToList() is { Count: > 0 } all)
            {
                var label = new FormattedText(
                    mask.Label,
                    System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    11.0,
                    mask.IsActive ? Brush : EffectBrush,
                    pixelsPerDip);
                Point corner = At(new Vector2(all.Min(point => point.X), all.Max(point => point.Y)));
                var where = new Point(corner.X, corner.Y + Grip + 2);
                drawingContext.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), null, new Rect(where.X - 2, where.Y - 1, label.Width + 4, label.Height + 2));
                drawingContext.DrawText(label, where);
            }

            if (!mask.IsActive)
            {
                continue;
            }

            foreach ((Vector2 from, Vector2 to) in mask.Handles)
            {
                drawingContext.DrawLine(active, At(from), At(to));
                drawingContext.DrawEllipse(Brush, null, At(to), Grip - 1, Grip - 1);
            }

            foreach (Vector2 point in mask.Points)
            {
                Point at = At(point);
                drawingContext.DrawRectangle(Brushes.White, active, new Rect(at.X - Grip, at.Y - Grip, Grip * 2.0, Grip * 2.0));
            }

            if (mask.Feather is { } feather && mask.FeatherBase is { } featherBase)
            {
                Point knob = At(feather);
                drawingContext.DrawLine(quiet, At(featherBase), knob);
                var diamond = new StreamGeometry();
                using (StreamGeometryContext context = diamond.Open())
                {
                    context.BeginFigure(new Point(knob.X, knob.Y - Grip - 1), isFilled: true, isClosed: true);
                    context.PolyLineTo([new Point(knob.X + Grip + 1, knob.Y), new Point(knob.X, knob.Y + Grip + 1), new Point(knob.X - Grip - 1, knob.Y)], isStroked: true, isSmoothJoin: false);
                }

                diamond.Freeze();
                drawingContext.DrawGeometry(Brushes.White, active, diamond);
            }
        }

        DrawSketch(drawingContext, active, shade, At);
    }

    private void DrawSketch(DrawingContext drawingContext, Pen pen, Pen shade, Func<Vector2, Point> at)
    {
        if (Sketch is not { Count: > 0 } sketch)
        {
            return;
        }

        if (Tool is MaskTool.Rectangle or MaskTool.Ellipse && sketch.Count == 2)
        {
            var box = new Rect(at(sketch[0]), at(sketch[1]));
            if (Tool == MaskTool.Rectangle)
            {
                drawingContext.DrawRectangle(null, shade, box);
                drawingContext.DrawRectangle(null, pen, box);
            }
            else
            {
                var centre = new Point(box.X + (box.Width / 2), box.Y + (box.Height / 2));
                drawingContext.DrawEllipse(null, shade, centre, box.Width / 2, box.Height / 2);
                drawingContext.DrawEllipse(null, pen, centre, box.Width / 2, box.Height / 2);
            }

            return;
        }

        for (int index = 1; index < sketch.Count; index++)
        {
            drawingContext.DrawLine(pen, at(sketch[index - 1]), at(sketch[index]));
        }

        foreach (Vector2 point in sketch)
        {
            Point here = at(point);
            drawingContext.DrawRectangle(Brushes.White, pen, new Rect(here.X - Grip, here.Y - Grip, Grip * 2.0, Grip * 2.0));
        }
    }
}
