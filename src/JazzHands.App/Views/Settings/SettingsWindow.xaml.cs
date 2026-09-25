using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace JazzHands.App.Views.Settings;

/// <summary>The Settings dialog. Which page shows is the window's; everything else is its viewmodel's.</summary>
public partial class SettingsWindow : Window
{
    /// <summary>Creates the window.</summary>
    public SettingsWindow() => InitializeComponent();

    /// <summary>Opens on the keymap page, for Help, Keyboard shortcuts.</summary>
    public void ShowKeymap() => Pages.SelectedItem = KeymapPage;
}

/// <summary>Visible when a list's selected index is the page number given as the parameter.</summary>
public sealed class PageIsConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int index && int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int page) && index == page
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// One of a pair of radio buttons over a bool: checked when the value is the parameter (<c>True</c> or
/// <c>False</c>), and setting it only when checked, so the button the group unchecks writes nothing back
/// and the two cannot flip each other for ever.
/// </summary>
public sealed class RadioBoolConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool flag && flag == IsTrue(parameter);

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? IsTrue(parameter) : Binding.DoNothing;

    private static bool IsTrue(object parameter) => string.Equals(parameter as string, "True", StringComparison.OrdinalIgnoreCase);
}
