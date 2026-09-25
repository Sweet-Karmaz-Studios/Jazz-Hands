using System.Globalization;
using System.Windows;
using System.Windows.Data;
using JazzHands.App.Shell;

namespace JazzHands.App.Views.Shell;

/// <summary>The drawing for a taskbar badge: <c>Icon.Badge.Exporting</c> and the rest in Icons.xaml.</summary>
public sealed class BadgeImageConverter : IValueConverter
{
    /// <summary>The one instance, for <c>x:Static</c> where a resource is not yet in reach.</summary>
    public static BadgeImageConverter Instance { get; } = new();

    /// <inheritdoc />
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DesktopBadge badge and not DesktopBadge.None
            ? Application.Current?.TryFindResource($"Icon.Badge.{badge}")
            : null;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
