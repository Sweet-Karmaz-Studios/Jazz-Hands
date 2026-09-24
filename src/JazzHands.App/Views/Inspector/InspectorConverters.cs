using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace JazzHands.App.Views.Inspector;

/// <summary>
/// Path data text to a geometry, and no text to no geometry: WPF's own conversion throws on null,
/// which a parameter that is not animated has.
/// </summary>
public sealed class PathDataConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } data ? Geometry.Parse(data) : null;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>
/// True when a value is the converter parameter, for a row of radio buttons over one setting;
/// checking a button sets the setting to its parameter.
/// </summary>
public sealed class EqualsConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}

/// <summary>An sRGB hex colour as a brush for a swatch; transparent when it does not read.</summary>
public sealed class HexBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            return value is string { Length: > 0 } hex && ColorConverter.ConvertFromString(hex.Length == 9 ? "#" + hex[7..9] + hex[1..7] : hex) is Color colour
                ? new SolidColorBrush(colour)
                : Brushes.Transparent;
        }
        catch (FormatException)
        {
            return Brushes.Transparent;
        }
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
