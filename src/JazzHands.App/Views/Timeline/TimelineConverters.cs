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
