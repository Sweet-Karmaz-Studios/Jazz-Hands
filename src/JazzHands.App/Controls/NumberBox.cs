using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JazzHands.App.Controls;

/// <summary>
/// A number that is typed or dragged: drag sideways to scrub it, Shift for ten times the step,
/// Alt for a tenth, click without dragging to type, Enter to take what was typed, Escape to put it
/// back, the arrow keys to step. The unit is shown after the number and ignored when typed.
/// </summary>
/// <remarks>
/// Every value it takes goes to <see cref="Value"/>, which a view model turns into a command; a
/// drag sends one per step it moves, and the undo stack folds a drag of one parameter into one
/// step. Parsing and formatting are static so they can be tested without a window.
/// </remarks>
public sealed class NumberBox : TextBox
{
    /// <summary>The number.</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(NumberBox),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (sender, _) => ((NumberBox)sender).ShowValue()));

    /// <summary>The smallest value; NaN for none.</summary>
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(NumberBox), new PropertyMetadata(double.NaN));

    /// <summary>The largest value; NaN for none.</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(NumberBox), new PropertyMetadata(double.NaN));

    /// <summary>How much one pixel of drag, or one arrow press, changes the value.</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(double), typeof(NumberBox), new PropertyMetadata(1.0));

    /// <summary>How many decimals are shown.</summary>
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(NumberBox), new PropertyMetadata(2, (sender, _) => ((NumberBox)sender).ShowValue()));

    /// <summary>The unit shown after the number.</summary>
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(NumberBox), new PropertyMetadata(string.Empty, (sender, _) => ((NumberBox)sender).ShowValue()));

    private Point? _pressed;
    private double _startValue;
    private bool _scrubbing;

    /// <summary>Creates a box showing zero.</summary>
    public NumberBox()
    {
        Cursor = Cursors.SizeWE;
        ShowValue();
    }

    /// <summary>The number.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>The smallest value; NaN for none.</summary>
    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>The largest value; NaN for none.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>How much one pixel of drag changes the value.</summary>
    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    /// <summary>How many decimals are shown.</summary>
    public int Decimals
    {
        get => (int)GetValue(DecimalsProperty);
        set => SetValue(DecimalsProperty, value);
    }

    /// <summary>The unit shown after the number.</summary>
    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    /// <summary>A number as the box shows it: trailing zeros dropped, the unit after a space.</summary>
    public static string Format(double value, int decimals, string unit)
    {
        string number = Math.Round(value, Math.Clamp(decimals, 0, 6)).ToString("0." + new string('#', Math.Clamp(decimals, 0, 6)), CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(unit) ? number : $"{number} {unit}";
    }

    /// <summary>Reads what was typed, with or without the unit.</summary>
    public static bool TryParse(string? text, string unit, out double value)
    {
        string trimmed = (text ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(unit) && trimmed.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^unit.Length].Trim();
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    /// <summary>A value held inside the limits, where there are any.</summary>
    public static double Clamp(double value, double minimum, double maximum)
    {
        if (!double.IsNaN(minimum) && value < minimum)
        {
            value = minimum;
        }

        if (!double.IsNaN(maximum) && value > maximum)
        {
            value = maximum;
        }

        return value;
    }

    /// <summary>What a drag of <paramref name="pixels"/> makes of a starting value.</summary>
    public static double Scrub(double start, double pixels, double step, ModifierKeys modifiers, double minimum, double maximum)
    {
        double factor = modifiers.HasFlag(ModifierKeys.Shift) ? 10.0 : modifiers.HasFlag(ModifierKeys.Alt) ? 0.1 : 1.0;
        return Clamp(start + (pixels * step * factor), minimum, maximum);
    }

    /// <inheritdoc />
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewMouseLeftButtonDown(e);

        if (IsKeyboardFocusWithin || !IsEnabled)
        {
            return;
        }

        _pressed = e.GetPosition(this);
        _startValue = Value;
        _scrubbing = false;
        CaptureMouse();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewMouseMove(e);

        if (_pressed is not { } pressed || !IsMouseCaptured)
        {
            return;
        }

        double moved = e.GetPosition(this).X - pressed.X;
        if (!_scrubbing && Math.Abs(moved) < SystemParameters.MinimumHorizontalDragDistance)
        {
            return;
        }

        _scrubbing = true;
        Value = Scrub(_startValue, moved, Step, Keyboard.Modifiers, Minimum, Maximum);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewMouseLeftButtonUp(e);

        if (_pressed is null)
        {
            return;
        }

        _pressed = null;
        ReleaseMouseCapture();

        // A click with no drag is a request to type.
        if (!_scrubbing)
        {
            Cursor = Cursors.IBeam;
            Focus();
            SelectAll();
        }

        _scrubbing = false;
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        switch (e.Key)
        {
            case Key.Enter:
                Commit();
                SelectAll();
                e.Handled = true;
                return;
            case Key.Escape:
                ShowValue(force: true);
                SelectAll();
                e.Handled = true;
                return;
            case Key.Up:
                Value = Clamp(Value + Step, Minimum, Maximum);
                ShowValue(force: true);
                e.Handled = true;
                return;
            case Key.Down:
                Value = Clamp(Value - Step, Minimum, Maximum);
                ShowValue(force: true);
                e.Handled = true;
                return;
        }

        base.OnKeyDown(e);
    }

    /// <inheritdoc />
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Commit();
        Cursor = Cursors.SizeWE;
        ShowValue(force: true);
    }

    private void Commit()
    {
        if (TryParse(Text, Unit, out double typed))
        {
            Value = Clamp(typed, Minimum, Maximum);
        }

        ShowValue(force: true);
    }

    private void ShowValue(bool force = false)
    {
        // Not while someone is typing, or the number they are halfway through would be replaced.
        if (force || !IsKeyboardFocusWithin)
        {
            Text = Format(Value, Decimals, Unit);
        }
    }
}
