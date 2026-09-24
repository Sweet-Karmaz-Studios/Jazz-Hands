using System.Globalization;
using System.Numerics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.App.ViewModels.Inspector;

/// <summary>What a parameter row asks of the panel it is in: every edit becomes a command there.</summary>
public interface IParamEditor
{
    /// <summary>A new value, as the text the commands read.</summary>
    void Send(ParamRowViewModel row, string text);

    /// <summary>The stopwatch: turn animation on at the playhead, or off keeping the value there.</summary>
    void ToggleAnimation(ParamRowViewModel row);

    /// <summary>The diamond: add a keyframe at the playhead, or remove the one there.</summary>
    void ToggleKeyframe(ParamRowViewModel row);

    /// <summary>Move the playhead to the keyframe before or after it.</summary>
    void GoToKeyframe(ParamRowViewModel row, bool forward);

    /// <summary>Put the parameter back to its default, animation and all.</summary>
    void Reset(ParamRowViewModel row);

    /// <summary>Pick a point on the preview.</summary>
    void Pick(ParamRowViewModel row);
}

/// <summary>
/// One parameter in the inspector: a control for its type, the stopwatch, the keyframe diamond
/// and arrows, and a curve thumbnail when it is animated.
/// </summary>
/// <remarks>
/// The typed properties (<see cref="Number"/>, <see cref="X"/>, <see cref="Flag"/> and the rest)
/// are what the controls bind to. Setting one sends the whole value through
/// <see cref="IParamEditor.Send"/>; <see cref="Load"/> sets them from the project without sending
/// anything, which is how a change made elsewhere (the CLI, MCP, an undo) shows up here.
/// </remarks>
public sealed partial class ParamRowViewModel : ObservableObject
{
    private readonly IParamEditor _editor;
    private bool _loading;

    [ObservableProperty]
    private double _number;

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    [ObservableProperty]
    private double _z;

    [ObservableProperty]
    private double _w;

    [ObservableProperty]
    private bool _flag;

    [ObservableProperty]
    private string _choice = string.Empty;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string _colorText = "#000000";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousKeyframeCommand), nameof(NextKeyframeCommand))]
    private bool _isAnimated;

    [ObservableProperty]
    private bool _hasKeyframeHere;

    [ObservableProperty]
    private bool _isDefault = true;

    [ObservableProperty]
    private bool _isMixed;

    [ObservableProperty]
    private string? _curveData;

    /// <summary>Creates a row for one parameter of one owner.</summary>
    public ParamRowViewModel(IParamEditor editor, string ownerId, ParamDescriptor descriptor, string section)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(descriptor);

        _editor = editor;
        OwnerId = ownerId;
        Descriptor = descriptor;
        Section = section;
    }

    /// <summary>The clip, effect or mask the parameter belongs to.</summary>
    public string OwnerId { get; }

    /// <summary>What the parameter is.</summary>
    public ParamDescriptor Descriptor { get; }

    /// <summary>The section it is shown in.</summary>
    public string Section { get; }

    /// <summary>The name the commands use.</summary>
    public string Name => Descriptor.Name;

    /// <summary>What the row is called.</summary>
    public string Label => Descriptor.DisplayName;

    /// <summary>The description, for a tooltip.</summary>
    public string Description => Descriptor.Description;

    /// <summary>The unit after the number.</summary>
    public string Unit => Descriptor.Unit;

    /// <summary>The kind of control.</summary>
    public ParamType Type => Descriptor.Type;

    /// <summary>False for a parameter with no stopwatch.</summary>
    public bool Animatable => Descriptor.Animatable;

    /// <summary>For an enum, what it takes.</summary>
    public IReadOnlyList<string> Choices => [.. Descriptor.Choices];

    /// <summary>The smallest value typed or dragged; NaN for none.</summary>
    public double Minimum => Descriptor.Min ?? double.NaN;

    /// <summary>The largest value typed or dragged; NaN for none.</summary>
    public double Maximum => Descriptor.Max ?? double.NaN;

    /// <summary>The slider's low end.</summary>
    public double SliderMinimum => Descriptor.SliderLow;

    /// <summary>The slider's high end.</summary>
    public double SliderMaximum => Descriptor.SliderHigh;

    /// <summary>True when the row has a slider as well as a number: a number with both ends.</summary>
    public bool HasSlider => Type is ParamType.Float or ParamType.Int && (Descriptor.Min is not null || Descriptor.SliderMax is not null) && (Descriptor.Max is not null || Descriptor.SliderMax is not null);

    /// <summary>How much a pixel of drag changes the number: a two-hundredth of the slider, rounded to a tidy step.</summary>
    public double Step
    {
        get
        {
            if (Type == ParamType.Int)
            {
                return 1.0;
            }

            double span = HasSlider ? SliderMaximum - SliderMinimum : 100.0;
            double raw = span / 200.0;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            return raw / magnitude >= 5 ? 5 * magnitude : raw / magnitude >= 2 ? 2 * magnitude : magnitude;
        }
    }

    /// <summary>How many decimals the number shows.</summary>
    public int Decimals => Type == ParamType.Int ? 0 : Math.Clamp((int)Math.Ceiling(-Math.Log10(Step)) + 1, 0, 4);

    /// <summary>A single number.</summary>
    public bool IsNumber => Type is ParamType.Float or ParamType.Int;

    /// <summary>Two numbers.</summary>
    public bool IsPair => Type is ParamType.Float2 or ParamType.Point;

    /// <summary>A place on the frame, which the preview can pick.</summary>
    public bool IsPoint => Type == ParamType.Point;

    /// <summary>Four numbers.</summary>
    public bool IsQuad => Type == ParamType.Float4;

    /// <summary>A colour.</summary>
    public bool IsColor => Type == ParamType.Color;

    /// <summary>A switch.</summary>
    public bool IsFlag => Type == ParamType.Bool;

    /// <summary>One of a list.</summary>
    public bool IsChoice => Type == ParamType.Enum;

    /// <summary>Free text or a path.</summary>
    public bool IsText => Type is ParamType.Text or ParamType.Path;

    /// <summary>The keyframes, on the sequence, for the arrows.</summary>
    public IReadOnlyList<Flicks> KeyframeTimes { get; private set; } = [];

    /// <summary>The value as the commands read it.</summary>
    public string ValueText => Type switch
    {
        ParamType.Float => Invariant(Number),
        ParamType.Int => Invariant(Math.Round(Number)),
        ParamType.Float2 or ParamType.Point => $"{Invariant(X)}, {Invariant(Y)}",
        ParamType.Float4 => $"{Invariant(X)}, {Invariant(Y)}, {Invariant(Z)}, {Invariant(W)}",
        ParamType.Color => ColorText,
        ParamType.Bool => Flag ? "true" : "false",
        ParamType.Enum => Choice,
        _ => Text,
    };

    /// <summary>
    /// Shows a value from the project without sending it back: what the parameter is worth at
    /// the playhead, whether it is animated, and where its keyframes are.
    /// </summary>
    /// <param name="value">What it is worth at the playhead.</param>
    /// <param name="stored">What the project holds, null for the default.</param>
    /// <param name="origin">Where the owner's keyframe time zero is on the sequence.</param>
    /// <param name="length">How long the owner lasts, for the curve thumbnail.</param>
    /// <param name="playhead">Where the playhead is on the sequence.</param>
    /// <param name="tolerance">How near a keyframe has to be to count as at the playhead.</param>
    public void Load(ParamValue value, AnimatedValue? stored, Flicks origin, Flicks length, Flicks playhead, Flicks tolerance)
    {
        ArgumentNullException.ThrowIfNull(value);

        _loading = true;
        try
        {
            switch (value)
            {
                case ParamValue.Float number:
                    Number = number.Value;
                    break;
                case ParamValue.Int whole:
                    Number = whole.Value;
                    break;
                case ParamValue.Float2 pair:
                    X = pair.Value.X;
                    Y = pair.Value.Y;
                    break;
                case ParamValue.Float4 quad:
                    (X, Y, Z, W) = (quad.Value.X, quad.Value.Y, quad.Value.Z, quad.Value.W);
                    break;
                case ParamValue.Color colour:
                    ColorText = ParamValues.FormatColor(colour.Value);
                    break;
                case ParamValue.Bool flag:
                    Flag = flag.Value;
                    break;
                case ParamValue.Enum member:
                    Choice = member.Value;
                    break;
                case ParamValue.Text text:
                    Text = text.Value;
                    break;
                case ParamValue.Path path:
                    Text = path.Value;
                    break;
            }

            IsDefault = stored is null;
            IsAnimated = stored is KeyframedValue { IsAnimated: true };
            KeyframeTimes = stored is KeyframedValue keyed ? [.. keyed.Keyframes.Select(key => key.Time + origin)] : [];
            HasKeyframeHere = KeyframeTimes.Any(time => Math.Abs((time - playhead).Value) <= tolerance.Value);
            CurveData = IsAnimated && IsNumber ? Curve(stored!, length) : null;
            PreviousKeyframeCommand.NotifyCanExecuteChanged();
            NextKeyframeCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The keyframe before a time, on the sequence, or null.</summary>
    public Flicks? KeyframeBefore(Flicks playhead, Flicks tolerance) =>
        KeyframeTimes.Where(time => time < playhead - tolerance).Select(time => (Flicks?)time).LastOrDefault();

    /// <summary>The keyframe after a time, on the sequence, or null.</summary>
    public Flicks? KeyframeAfter(Flicks playhead, Flicks tolerance) =>
        KeyframeTimes.Where(time => time > playhead + tolerance).Select(time => (Flicks?)time).FirstOrDefault();

    partial void OnNumberChanged(double value) => Edited();

    partial void OnXChanged(double value) => Edited();

    partial void OnYChanged(double value) => Edited();

    partial void OnZChanged(double value) => Edited();

    partial void OnWChanged(double value) => Edited();

    partial void OnFlagChanged(bool value) => Edited();

    partial void OnChoiceChanged(string value) => Edited();

    partial void OnTextChanged(string value) => Edited();

    partial void OnColorTextChanged(string value)
    {
        OnPropertyChanged(nameof(Swatch));
        Edited();
    }

    /// <summary>
    /// The colour as WPF reads it, #AARRGGBB, for the swatch: the text is #RRGGBBAA, which WPF
    /// would read with the channels shifted.
    /// </summary>
    public string Swatch
    {
        get
        {
            string hex = ColorText.TrimStart('#');
            return hex.Length switch
            {
                6 => $"#FF{hex}",
                8 => $"#{hex[6..]}{hex[..6]}",
                _ => "#00000000",
            };
        }
    }

    [RelayCommand]
    private void CommitText() => Edited();

    [RelayCommand]
    private void ToggleAnimation() => _editor.ToggleAnimation(this);

    [RelayCommand]
    private void ToggleKeyframe() => _editor.ToggleKeyframe(this);

    [RelayCommand(CanExecute = nameof(IsAnimated))]
    private void PreviousKeyframe() => _editor.GoToKeyframe(this, forward: false);

    [RelayCommand(CanExecute = nameof(IsAnimated))]
    private void NextKeyframe() => _editor.GoToKeyframe(this, forward: true);

    [RelayCommand]
    private void Reset() => _editor.Reset(this);

    [RelayCommand]
    private void Pick() => _editor.Pick(this);

    private void Edited()
    {
        if (!_loading)
        {
            _editor.Send(this, ValueText);
        }
    }

    private static string Invariant(double value) => ((float)value).ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// The curve over the owner's length as path data in a 40 by 16 box, highest value at the
    /// top, for the thumbnail beside an animated number.
    /// </summary>
    private string Curve(AnimatedValue stored, Flicks length)
    {
        const int Samples = 40;
        long span = Math.Max(1, Math.Max(length.Value, stored is KeyframedValue keyed ? keyed.End.Value : 1));
        var values = new float[Samples + 1];

        for (int index = 0; index <= Samples; index++)
        {
            values[index] = ParamEval.Eval(stored, Descriptor, new Flicks(span * index / Samples)) is ParamValue.Float number ? number.Value : 0.0f;
        }

        float low = values.Min();
        float high = values.Max();
        float range = high - low > 1e-6f ? high - low : 1.0f;

        var path = new StringBuilder("M");
        for (int index = 0; index <= Samples; index++)
        {
            double x = 40.0 * index / Samples;
            double y = 15.0 - (14.0 * (values[index] - low) / range);
            path.Append(CultureInfo.InvariantCulture, $" {x:0.##},{y:0.##}");
            if (index == 0)
            {
                path.Append(" L");
            }
        }

        return path.ToString();
    }

    /// <summary>A point from a pick, as the text the commands read.</summary>
    internal static string PointText(Vector2 point) => ParamValues.Format(new ParamValue.Float2(MathF.Round(point.X), MathF.Round(point.Y)));
}
