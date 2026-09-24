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
