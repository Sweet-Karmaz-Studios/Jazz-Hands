using System.Globalization;
using System.Windows.Data;

namespace JazzHands.App.Views;

/// <summary>
/// Names shown as menu items as they are: a menu item reads an underscore as the mark before its
/// access key, so "multicam_a" showed as "multicama". Names from the project, files and plugins
/// go through here; headers written with an access key ("_File") do not.
/// </summary>
public static class MenuText
{
    /// <summary>The name with each underscore doubled, which a menu item shows as one.</summary>
    public static string Escape(string? name) => (name ?? string.Empty).Replace("_", "__", StringComparison.Ordinal);
}

/// <summary>A bound name as a menu item's header, its underscores kept (<see cref="MenuText"/>).</summary>
public sealed class MenuTextConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        MenuText.Escape(value as string);

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
