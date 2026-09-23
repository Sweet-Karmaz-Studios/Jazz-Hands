using System.Globalization;
using System.Windows.Data;
using JazzHands.App.ViewModels.Playback;
using JazzHands.Core.Commands;

namespace JazzHands.App.Views.Playback;

/// <summary>Names a zoom the way the menu shows it: Fit, 100%, 200%.</summary>
public sealed class ZoomNameConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        PreviewZoom.Actual => "100%",
        PreviewZoom.Double => "200%",
        _ => "Fit",
    };

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Zoom names are shown, never edited.");
}

/// <summary>Names a preview quality in sentence case.</summary>
public sealed class QualityNameConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        PreviewQuality.Full => "Full",
        PreviewQuality.Half => "Half",
        PreviewQuality.Quarter => "Quarter",
        _ => "Auto",
    };

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Quality names are shown, never edited.");
}
