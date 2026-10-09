using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace JazzHands.App.Views.Timeline;

/// <summary>
/// One of two glyphs by a boolean: the parameter is "on|off", so a lock shows locked or open
/// without a ToggleButton, whose binding a click would break.
/// </summary>
public sealed class GlyphSwitchConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string[] glyphs = (parameter as string ?? string.Empty).Split('|');
        bool on = value is true;

        return glyphs.Length == 2 ? (on ? glyphs[0] : glyphs[1]) : string.Empty;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A track's colour, as the project stores it, to a brush.</summary>
public sealed class ColorBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string text = value as string ?? string.Empty;

        if (!Brushes.TryGetValue(text, out SolidColorBrush? brush))
        {
            Color color;
            try
            {
                color = ColorConverter.ConvertFromString(text) is Color parsed ? parsed : Colors.SteelBlue;
            }
            catch (FormatException)
            {
                color = Colors.SteelBlue;
            }

            brush = new SolidColorBrush(color);
            brush.Freeze();
            Brushes[text] = brush;
        }

        return brush;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A colour, as the project stores it, to a brush for text on the panels: lightened towards white
/// just as far as it takes to read (4.5 to 1 against the panel's #222222), so a dark role colour
/// such as Video's slate still reads as a word under a track's name.
/// </summary>
public sealed class ReadableColorBrushConverter : IValueConverter
{
    /// <summary>The contrast text wants against the panel.</summary>
    public const double Contrast = 4.5;

    private static readonly Color Panel = Color.FromRgb(0x22, 0x22, 0x22);
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string text = value as string ?? string.Empty;

        if (!Brushes.TryGetValue(text, out SolidColorBrush? brush))
        {
            Color color;
            try
            {
                color = ColorConverter.ConvertFromString(text) is Color parsed ? parsed : Colors.SteelBlue;
            }
            catch (FormatException)
            {
                color = Colors.SteelBlue;
            }

            brush = new SolidColorBrush(Readable(color));
            brush.Freeze();
            Brushes[text] = brush;
        }

        return brush;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>The colour, lightened in steps of a twentieth towards white until it reads on the panel.</summary>
    public static Color Readable(Color color)
    {
        Color lighter = color;
        for (int step = 1; step <= 20 && ContrastOf(lighter, Panel) < Contrast; step++)
        {
            double mix = step / 20.0;
            lighter = Color.FromRgb(Toward(color.R, mix), Toward(color.G, mix), Toward(color.B, mix));
        }

        return lighter;
    }

    /// <summary>The WCAG contrast ratio of two colours.</summary>
    public static double ContrastOf(Color a, Color b)
    {
        double one = Luminance(a);
        double two = Luminance(b);
        return (Math.Max(one, two) + 0.05) / (Math.Min(one, two) + 0.05);
    }

    private static byte Toward(byte channel, double mix) => (byte)Math.Round(channel + ((255 - channel) * mix));

    private static double Luminance(Color color) =>
        (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));

    private static double Linear(byte channel)
    {
        double value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}

/// <summary>True when the bound value equals the parameter: which tool button shows as picked.</summary>
public sealed class EqualsConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Equals(value, parameter);

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
