using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JazzHands.App.Controls;

/// <summary>
/// A colour wheel: a disc of hues with a puck to push towards one, as on every grading panel.
/// </summary>
/// <remarks>
/// <see cref="Puck"/> is in the unit disc, across and up; the disc's hues are laid out on the Cb/Cr
/// plane, so they sit where the vectorscope puts them. The puck moves by how far the mouse moves,
/// not to where it is, which is what makes small corrections possible: Shift for a tenth of the
/// speed, Alt for double. Double-click, Backspace or Delete puts it back in the centre.
/// <see cref="IsDragging"/> is true while the mouse is down, so a panel can hold off reloading
/// under the pointer.
/// </remarks>
public sealed class ColorWheel : FrameworkElement
{
    /// <summary>The puck, in the unit disc: X across, Y up.</summary>
    public static readonly DependencyProperty PuckProperty = DependencyProperty.Register(
        nameof(Puck),
        typeof(Point),
        typeof(ColorWheel),
        new FrameworkPropertyMetadata(default(Point), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>True while the puck is being dragged.</summary>
    public static readonly DependencyProperty IsDraggingProperty = DependencyProperty.Register(
        nameof(IsDragging),
        typeof(bool),
        typeof(ColorWheel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private static readonly Lazy<BitmapSource> Disc = new(DrawDisc);
    private Point _pressedAt;
    private Point _puckAtPress;

    /// <summary>Creates a wheel.</summary>
    public ColorWheel()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
    }

    /// <summary>The puck, in the unit disc.</summary>
    public Point Puck
    {
        get => (Point)GetValue(PuckProperty);
        set => SetValue(PuckProperty, value);
    }

    /// <summary>True while the puck is being dragged.</summary>
    public bool IsDragging
    {
        get => (bool)GetValue(IsDraggingProperty);
        set => SetValue(IsDraggingProperty, value);
    }

    /// <summary>The disc's picture: hues round, saturation out, BT.709 as the vectorscope draws them.</summary>
    public static BitmapSource DiscPicture => Disc.Value;

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double side = Math.Min(
            double.IsInfinity(availableSize.Width) ? 140 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 140 : availableSize.Height);
        return new Size(side, side);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        double radius = (Math.Min(ActualWidth, ActualHeight) / 2) - 2;
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        var bounds = new Rect(centre.X - radius, centre.Y - radius, radius * 2, radius * 2);

        drawingContext.DrawImage(Disc.Value, bounds);

        var ring = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0x10, 0x10, 0x10)), 1.5);
        ring.Freeze();
        drawingContext.DrawEllipse(null, ring, centre, radius, radius);

        var cross = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 1);
        cross.Freeze();
        drawingContext.DrawLine(cross, new Point(centre.X - 6, centre.Y), new Point(centre.X + 6, centre.Y));
        drawingContext.DrawLine(cross, new Point(centre.X, centre.Y - 6), new Point(centre.X, centre.Y + 6));

        var puck = new Point(centre.X + (Puck.X * radius), centre.Y - (Puck.Y * radius));
        var outline = new Pen(Brushes.Black, 1.5);
        outline.Freeze();
        var fill = new Pen(IsKeyboardFocused ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)), 2);
        fill.Freeze();
        drawingContext.DrawEllipse(null, outline, puck, 6.5, 6.5);
        drawingContext.DrawEllipse(null, fill, puck, 5, 5);
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonDown(e);
        Focus();

        if (e.ClickCount == 2)
        {
            Puck = default;
            e.Handled = true;
            return;
        }

        _pressedAt = e.GetPosition(this);
        _puckAtPress = Puck;
        IsDragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        if (!IsDragging)
        {
            return;
        }

        double radius = Math.Max(1, (Math.Min(ActualWidth, ActualHeight) / 2) - 2);
        double speed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? 2.0 : 1.0;
        Vector moved = (e.GetPosition(this) - _pressedAt) * (speed / radius);
        Puck = Held(new Point(_puckAtPress.X + moved.X, _puckAtPress.Y - moved.Y));
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonUp(e);
        if (IsDragging)
        {
            ReleaseMouseCapture();
            IsDragging = false;
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        IsDragging = false;
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);

        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.005 : 0.02;
        Point? next = e.Key switch
        {
            Key.Back or Key.Delete => default(Point),
            Key.Left => new Point(Puck.X - step, Puck.Y),
            Key.Right => new Point(Puck.X + step, Puck.Y),
            Key.Up => new Point(Puck.X, Puck.Y + step),
            Key.Down => new Point(Puck.X, Puck.Y - step),
            _ => null,
        };

        if (next is { } point)
        {
            Puck = Held(point);
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    /// <summary>A point kept inside the unit disc.</summary>
    private static Point Held(Point point)
    {
        double length = Math.Sqrt((point.X * point.X) + (point.Y * point.Y));
        return length > 1 ? new Point(point.X / length, point.Y / length) : point;
    }

    /// <summary>The disc, 256 pixels across: each point the colour a push that way gives, at mid grey.</summary>
    private static BitmapSource DrawDisc()
    {
        const int Size = 256;
        byte[] pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double u = ((x + 0.5) / Size * 2) - 1;
                double v = 1 - ((y + 0.5) / Size * 2);
                double length = Math.Sqrt((u * u) + (v * v));
                if (length > 1.0)
                {
                    continue;
                }

                // Mid grey pushed towards the hue at this angle, further out the further the push.
                double cb = u * 0.30;
                double cr = v * 0.30;
                double r = 0.5 + (1.5748 * cr);
                double g = 0.5 - (0.1873 * cb) - (0.4681 * cr);
                double b = 0.5 + (1.8556 * cb);

                // A soft edge, one pixel wide.
                double alpha = Math.Clamp((1.0 - length) * Size / 2, 0, 1);
                int at = ((y * Size) + x) * 4;
                pixels[at] = (byte)Math.Round(Math.Clamp(b, 0, 1) * 255 * alpha);
                pixels[at + 1] = (byte)Math.Round(Math.Clamp(g, 0, 1) * 255 * alpha);
                pixels[at + 2] = (byte)Math.Round(Math.Clamp(r, 0, 1) * 255 * alpha);
                pixels[at + 3] = (byte)Math.Round(alpha * 255);
            }
        }

        BitmapSource disc = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Pbgra32, null, pixels, Size * 4);
        disc.Freeze();
        return disc;
    }
}
