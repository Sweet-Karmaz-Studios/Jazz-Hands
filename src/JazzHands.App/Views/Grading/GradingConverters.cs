using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace JazzHands.App.Views.Grading;

/// <summary>Visible when false: the "add an effect" buttons, shown until the effect is there.</summary>
public sealed class InverseVisibility : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A number's negative: a slider's minimum from its range.</summary>
public sealed class Negate : IValueConverter
{
    /// <summary>The one instance.</summary>
    public static Negate Instance { get; } = new();

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double number ? -number : 0.0;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when two bound values are the same object: a tab against the selected one.</summary>
public sealed class SameItem : IMultiValueConverter
{
    /// <summary>The one instance.</summary>
    public static SameItem Instance { get; } = new();

    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [var first, var second] && ReferenceEquals(first, second);

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => [];
}

/// <summary>A curve's colour by its channel: red, green and blue for theirs, light grey otherwise.</summary>
public sealed class CurveStroke : IValueConverter
{
    /// <summary>The one instance.</summary>
    public static CurveStroke Instance { get; } = new();

    private static readonly Brush Red = Frozen(Color.FromRgb(0xE0, 0x50, 0x50));
    private static readonly Brush Green = Frozen(Color.FromRgb(0x50, 0xD0, 0x60));
    private static readonly Brush Blue = Frozen(Color.FromRgb(0x50, 0x80, 0xF0));
    private static readonly Brush Other = Frozen(Color.FromRgb(0xE6, 0xE6, 0xE6));

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        "red" => Red,
        "green" => Green,
        "blue" => Blue,
        _ => Other,
    };

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>A node view connection as a curve: out of the right edge, into the left, or up into a node's foot for a key.</summary>
public sealed class NodeLinkGeometry : IValueConverter
{
    /// <summary>The one instance.</summary>
    public static NodeLinkGeometry Instance { get; } = new();

    /// <summary>A curve from one point to another, leaving rightwards and arriving rightwards, or upwards for a key.</summary>
    public static Geometry Curve(Point start, Point end, bool key)
    {
        double reach = Math.Max(20, Math.Abs(end.X - start.X) / 2);
        var figure = new PathFigure { StartPoint = start, IsFilled = false };
        figure.Segments.Add(new BezierSegment(
            new Point(start.X + reach, start.Y),
            key ? new Point(end.X, end.Y + 24) : new Point(end.X - reach, end.Y),
            end,
            isStroked: true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ViewModels.Grading.NodeLink link ? Curve(link.Start, link.End, link.IsKey) : Geometry.Empty;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
