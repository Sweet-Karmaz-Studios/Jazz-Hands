using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace JazzHands.App.Views.Media;

/// <summary>
/// Turns a colour label's name into the brush the theme defines for it.
/// </summary>
/// <remarks>
/// The names come from the model, where they are plain strings so that <c>media.set --color
/// blue</c> means the same thing from the CLI as it does from the context menu. The theme decides
/// what blue looks like.
/// </remarks>
public sealed class ColorLabelConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string name = value as string ?? string.Empty;
        string key = name.Length == 0
            ? "Brush.Label.None"
            : "Brush.Label." + char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();

        return Application.Current?.TryFindResource(key) as Brush
            ?? Application.Current?.TryFindResource("Brush.Label.None") as Brush
            ?? Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A brush does not become a colour label.");
}

/// <summary>
/// Binds one radio button or toggle to one value of an enum.
/// </summary>
/// <remarks>
/// Returning <see cref="Binding.DoNothing"/> when a button is unchecked is what keeps the group
/// working: only the button being turned on writes back, so the property is never briefly set to
/// whatever was deselected.
/// </remarks>
public sealed class EnumMatchConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null &&
        parameter is string expected &&
        string.Equals(value.ToString(), expected, StringComparison.Ordinal);

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is not string name)
        {
            return Binding.DoNothing;
        }

        Type type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return type.IsEnum && Enum.TryParse(type, name, ignoreCase: false, out object? parsed)
            ? parsed
            : Binding.DoNothing;
    }
}
