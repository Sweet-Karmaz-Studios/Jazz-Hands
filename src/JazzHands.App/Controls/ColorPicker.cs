using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace JazzHands.App.Controls;

/// <summary>
/// A colour picker: a square of saturation across and brightness up for the hue chosen on the
/// strip under it, and a strip for alpha under that, as every paint program has.
/// </summary>
/// <remarks>
/// <see cref="Hex"/> is the colour as the Inspector writes it, <c>#RRGGBB</c> or
/// <c>#RRGGBBAA</c> in sRGB with straight alpha, and it is written on every step of a drag, so a
/// row bound to it sends the colour as the pointer moves (its commands merge into one undo step).
/// The hue is kept while the colour is grey or black, where the hex alone cannot say it, so
/// dragging back out of a corner finds the hue it left.
/// </remarks>
public sealed class ColorPicker : FrameworkElement
{
    /// <summary>The colour, #RRGGBB or #RRGGBBAA.</summary>
    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(
        nameof(Hex),
        typeof(string),
        typeof(ColorPicker),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHexChanged));

    /// <summary>The square's height; the strips sit under it.</summary>
    internal const double SquareHeight = 150;

    /// <summary>Each strip's height.</summary>
    internal const double StripHeight = 14;

    /// <summary>The space between the square and the strips.</summary>
    internal const double Gap = 8;

    private static readonly Brush Checker = MakeChecker();
    private Part _dragging;
    private bool _writing;

    /// <summary>Creates a picker.</summary>
    public ColorPicker()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Cross;
        Width = 220;
        Height = SquareHeight + Gap + StripHeight + Gap + StripHeight;
        (Hue, Saturation, Value, Alpha) = FromHex(Hex) ?? (0, 0, 1, 1);
    }

    /// <summary>What a point is on.</summary>
    internal enum Part
    {
        /// <summary>Nothing.</summary>
        None,

        /// <summary>The saturation and brightness square.</summary>
        Square,

        /// <summary>The hue strip.</summary>
        Hue,

        /// <summary>The alpha strip.</summary>
        Alpha,
    }

    /// <summary>The colour, #RRGGBB or #RRGGBBAA.</summary>
    public string Hex
    {
        get => (string)GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    /// <summary>The hue, 0 to 360.</summary>
    internal double Hue { get; private set; }

    /// <summary>The saturation, 0 to 1.</summary>
    internal double Saturation { get; private set; }

    /// <summary>The brightness, 0 to 1.</summary>
    internal double Value { get; private set; }

    /// <summary>The alpha, 0 to 1.</summary>
    internal double Alpha { get; private set; }

    /// <summary>Reads #RRGGBB or #RRGGBBAA as hue, saturation, value and alpha, or null.</summary>
    internal static (double H, double S, double V, double A)? FromHex(string? text)
    {
        string hex = (text ?? string.Empty).Trim().TrimStart('#');
        if ((hex.Length != 6 && hex.Length != 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packed))
        {
            return null;
        }

        if (hex.Length == 6)
        {
            packed = (packed << 8) | 0xFF;
        }

        double r = ((packed >> 24) & 0xFF) / 255.0;
        double g = ((packed >> 16) & 0xFF) / 255.0;
        double b = ((packed >> 8) & 0xFF) / 255.0;
        double a = (packed & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double chroma = max - min;
        double hue = chroma == 0 ? 0
            : max == r ? 60 * (((g - b) / chroma) % 6)
            : max == g ? 60 * (((b - r) / chroma) + 2)
            : 60 * (((r - g) / chroma) + 4);
        return (hue < 0 ? hue + 360 : hue, max == 0 ? 0 : chroma / max, max, a);
    }

    /// <summary>Writes hue, saturation, value and alpha as #RRGGBB, with AA when not opaque.</summary>
    internal static string ToHex(double hue, double saturation, double value, double alpha)
    {
        (double r, double g, double b) = ToRgb(hue, saturation, value);
        static byte Byte(double channel) => (byte)Math.Round(Math.Clamp(channel, 0, 1) * 255);
        string rgb = string.Create(CultureInfo.InvariantCulture, $"#{Byte(r):X2}{Byte(g):X2}{Byte(b):X2}");
        byte opacity = Byte(alpha);
        return opacity == 255 ? rgb : string.Create(CultureInfo.InvariantCulture, $"{rgb}{opacity:X2}");
    }

    /// <summary>What a point in the picker is on.</summary>
    internal Part PartAt(Point point) =>
        point.Y < SquareHeight + (Gap / 2) ? Part.Square
        : point.Y < SquareHeight + Gap + StripHeight + (Gap / 2) ? Part.Hue
        : Part.Alpha;

    /// <summary>Sets the colour from a point on a part, as a press or a drag there does.</summary>
    internal void Pick(Part part, Point point)
    {
        double width = Math.Max(1, ActualWidth > 0 ? ActualWidth : Width);
        double across = Math.Clamp(point.X / width, 0, 1);
        switch (part)
        {
            case Part.Square:
                Saturation = across;
                Value = 1 - Math.Clamp(point.Y / SquareHeight, 0, 1);
                break;
            case Part.Hue:
                Hue = across * 360;
                break;
            case Part.Alpha:
                Alpha = across;
                break;
            default:
                return;
        }

        _writing = true;
        try
        {
            Hex = ToHex(Hue, Saturation, Value, Alpha);
        }
        finally
        {
            _writing = false;
        }

        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonDown(e);
        Point point = e.GetPosition(this);
        _dragging = PartAt(point);
        Focus();
        CaptureMouse();
        Pick(_dragging, point);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        if (_dragging != Part.None && IsMouseCaptured)
        {
            Pick(_dragging, e.GetPosition(this));
        }
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = Part.None;
        ReleaseMouseCapture();
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        double width = ActualWidth;
        var square = new Rect(0, 0, width, SquareHeight);
        var hues = new Rect(0, SquareHeight + Gap, width, StripHeight);
        var alphas = new Rect(0, hues.Bottom + Gap, width, StripHeight);

        // The square: the hue at full strength, white over it to the left, black under it downwards.
        (double r, double g, double b) = ToRgb(Hue, 1, 1);
        drawingContext.DrawRectangle(Frozen(new SolidColorBrush(Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255)))), null, square);
        drawingContext.DrawRectangle(Frozen(new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0)), null, square);
        drawingContext.DrawRectangle(Frozen(new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90)), null, square);

        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int stop = 0; stop <= 6; stop++)
        {
            (double hr, double hg, double hb) = ToRgb(stop * 60, 1, 1);
            rainbow.GradientStops.Add(new GradientStop(Color.FromRgb((byte)(hr * 255), (byte)(hg * 255), (byte)(hb * 255)), stop / 6.0));
        }

        drawingContext.DrawRectangle(Frozen(rainbow), null, hues);

        (double cr, double cg, double cb) = ToRgb(Hue, Saturation, Value);
        var opaque = Color.FromRgb((byte)Math.Round(cr * 255), (byte)Math.Round(cg * 255), (byte)Math.Round(cb * 255));
        drawingContext.DrawRectangle(Checker, null, alphas);
        drawingContext.DrawRectangle(Frozen(new LinearGradientBrush(Color.FromArgb(0, opaque.R, opaque.G, opaque.B), opaque, 0)), null, alphas);

        // The thumbs: a ring on the square, a bar on each strip, each black inside white so it shows on any colour.
        var outer = new Pen(Brushes.White, 2);
        var inner = new Pen(Brushes.Black, 1);
        var at = new Point(Saturation * width, (1 - Value) * SquareHeight);
        drawingContext.DrawEllipse(null, outer, at, 6, 6);
        drawingContext.DrawEllipse(null, inner, at, 7.5, 7.5);
        foreach ((Rect strip, double fraction) in (ReadOnlySpan<(Rect, double)>)[(hues, Hue / 360), (alphas, Alpha)])
        {
            var bar = new Rect((fraction * width) - 2, strip.Top - 2, 4, strip.Height + 4);
            drawingContext.DrawRectangle(null, inner, bar);
            drawingContext.DrawRectangle(null, outer, new Rect(bar.X + 1, bar.Y + 1, bar.Width - 2, bar.Height - 2));
        }
    }

    private static void OnHexChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        var picker = (ColorPicker)owner;
        if (picker._writing || FromHex(e.NewValue as string) is not { } colour)
        {
            return;
        }

        // A grey or black says nothing of its hue: keep the one there was.
        picker.Hue = colour.S == 0 || colour.V == 0 ? picker.Hue : colour.H;
        picker.Saturation = colour.V == 0 ? picker.Saturation : colour.S;
        picker.Value = colour.V;
        picker.Alpha = colour.A;
    }

    private static (double R, double G, double B) ToRgb(double hue, double saturation, double value)
    {
        double chroma = value * saturation;
        double sector = (hue % 360) / 60;
        double x = chroma * (1 - Math.Abs((sector % 2) - 1));
        (double r, double g, double b) = sector switch
        {
            < 1 => (chroma, x, 0.0),
            < 2 => (x, chroma, 0.0),
            < 3 => (0.0, chroma, x),
            < 4 => (0.0, x, chroma),
            < 5 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        double m = value - chroma;
        return (r + m, g + m, b + m);
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Brush MakeChecker()
    {
        var light = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        var dark = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A));
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        drawing.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 4, 4))));
        drawing.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(4, 4, 4, 4))));
        var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }
}
