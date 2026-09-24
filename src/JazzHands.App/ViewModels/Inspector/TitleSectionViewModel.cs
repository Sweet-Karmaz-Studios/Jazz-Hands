using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Core.Titles;

namespace JazzHands.App.ViewModels.Inspector;

/// <summary>A font family in the picker, drawn in itself.</summary>
/// <param name="Family">Its name.</param>
/// <param name="Note">Where it comes from when that matters: the project's fonts, or missing.</param>
public sealed record FontChoice(string Family, string Note)
{
    /// <inheritdoc />
    public override string ToString() => Family;
}

/// <summary>
/// The text section of the inspector for a title: what it says with bold, italic, underline and
/// colour over a selection, its font, weight and alignment, and how it comes in and goes out.
/// </summary>
/// <remarks>
/// Every change is one of the title commands, exactly as <c>jazz title</c> would send it: the text
/// and its spans are <c>title.set-text</c> (the editor turns what is typed into markup, and the
/// buttons restyle the selection with <see cref="TitleMarkup.Restyle"/>), the look is
/// <c>title.set-style</c>, and the pickers are <c>title.set-animation</c>. The rest of a title's
/// look (size, colour, outline, box, shadow) is ordinary parameter rows below it, so it can be
/// keyframed.
/// </remarks>
public sealed partial class TitleSectionViewModel : ObservableObject
{
    private readonly Func<ICommand, Task> _run;
    private readonly Func<IReadOnlyList<FontInfo>> _listFonts;
    private IReadOnlyList<FontChoice>? _fonts;
    private bool _loading;

    [ObservableProperty]
    private string _markup = string.Empty;

    [ObservableProperty]
    private bool _isTextAnimated;

    [ObservableProperty]
    private int _selectionStart;

    [ObservableProperty]
    private int _selectionLength;

    [ObservableProperty]
    private string _colourText = "#FFCC00";

    [ObservableProperty]
    private FontChoice? _font;

    [ObservableProperty]
    private string _weight = "bold";

    [ObservableProperty]
    private bool _italic;

    [ObservableProperty]
    private string _align = "centre";

    [ObservableProperty]
    private string _vAlign = "middle";

    [ObservableProperty]
    private string _animationIn = TitleAnimations.None;

    [ObservableProperty]
    private double _inSeconds = 0.5;

    [ObservableProperty]
    private string _animationOut = TitleAnimations.None;

    [ObservableProperty]
    private double _outSeconds = 0.5;

    [ObservableProperty]
    private double _size = 96;

    /// <summary>Creates the section.</summary>
    /// <param name="clipId">The title clip.</param>
    /// <param name="run">Sends a command, as the inspector does.</param>
    /// <param name="listFonts">The families <c>fonts.list</c> answers, asked the first time the picker opens.</param>
    public TitleSectionViewModel(string clipId, Func<ICommand, Task> run, Func<IReadOnlyList<FontInfo>> listFonts)
    {
        ArgumentException.ThrowIfNullOrEmpty(clipId);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(listFonts);

        ClipId = clipId;
        _run = run;
        _listFonts = listFonts;
    }

    /// <summary>The title clip.</summary>
    public string ClipId { get; }

    /// <summary>The weights, lightest first.</summary>
    public IReadOnlyList<string> Weights { get; } = ["thin", "extra-light", "light", "regular", "medium", "semibold", "bold", "extra-bold", "black"];

    /// <summary>Every animation, none first.</summary>
    public IReadOnlyList<string> Animations { get; } = TitleAnimations.Names;

    /// <summary>The families to choose from: the project's first, then those installed, and the title's own if it is missing.</summary>
    public IReadOnlyList<FontChoice> Fonts
    {
        get
        {
            if (_fonts is null)
            {
                IReadOnlyList<FontInfo> listed = _listFonts();
                _fonts = [.. listed.Select(font => new FontChoice(font.Family, font.Source == "project" ? "project" : string.Empty))];
            }

            return Font is { } current && !_fonts.Any(font => string.Equals(font.Family, current.Family, StringComparison.OrdinalIgnoreCase))
                ? [current, .. _fonts]
                : _fonts;
        }
    }

    /// <summary>Shows the title as the project has it at the playhead, sending nothing back.</summary>
    /// <param name="values">Its parameters at the playhead.</param>
    /// <param name="textAnimated">True when its text has keyframes, which the editor cannot change.</param>
    /// <param name="animation">Its animations as they were last chosen.</param>
    public void Load(ParameterSet values, bool textAnimated, TitleAnimation animation)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(animation);

        _loading = true;
        try
        {
            Markup = values.Text(TitleParams.Text);
            Size = values.Float(TitleParams.Size);
            IsTextAnimated = textAnimated;
            string family = values.Text(TitleParams.Font);
            if (!string.Equals(Font?.Family, family, StringComparison.Ordinal))
            {
                Font = _fonts?.FirstOrDefault(font => string.Equals(font.Family, family, StringComparison.OrdinalIgnoreCase)) ?? new FontChoice(family, string.Empty);
                OnPropertyChanged(nameof(Fonts));
            }

            Weight = values.Enum(TitleParams.Weight);
            Italic = values.Bool(TitleParams.Italic);
            Align = values.Enum(TitleParams.Align);
            VAlign = values.Enum(TitleParams.VAlign);
            AnimationIn = animation.In;
            InSeconds = Math.Round(animation.InDuration.ToSeconds(), 3);
            AnimationOut = animation.Out;
            OutSeconds = Math.Round(animation.OutDuration.ToSeconds(), 3);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The editor has new text: sends it when it differs from what the title says.</summary>
    public Task CommitTextAsync(string markup)
    {
        ArgumentNullException.ThrowIfNull(markup);
        if (IsTextAnimated || string.Equals(markup, Markup, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        Markup = markup;
        return _run(new SetTitleTextCommand(ClipId, markup));
    }

    /// <summary>True when every letter of the selection has what <paramref name="has"/> asks.</summary>
    public bool SelectionIs(Func<TitleStyle, bool> has)
    {
        ArgumentNullException.ThrowIfNull(has);
        TitleText text = TitleMarkup.Parse(Markup);
        int end = Math.Min(text.Plain.Length, SelectionStart + SelectionLength);
        return SelectionLength > 0 && Enumerable.Range(SelectionStart, Math.Max(0, end - SelectionStart)).All(index => has(text.StyleAt(index)));
    }

    /// <summary>The editor's text, sent as <c>title.set-text</c>.</summary>
    [RelayCommand]
    private Task CommitText(string markup) => CommitTextAsync(markup);

    /// <summary>Makes the selection bold, or plain again when all of it already is.</summary>
    [RelayCommand]
    private Task ToggleBold() => Toggle(style => style.Bold, (style, on) => style with { Bold = on });

    /// <summary>Makes the selection italic, or upright again.</summary>
    [RelayCommand]
    private Task ToggleItalic() => Toggle(style => style.Italic, (style, on) => style with { Italic = on });

    /// <summary>Underlines the selection, or takes the line away.</summary>
    [RelayCommand]
    private Task ToggleUnderline() => Toggle(style => style.Underline, (style, on) => style with { Underline = on });

    /// <summary>Gives the selection the colour typed beside the button.</summary>
    [RelayCommand]
    private Task ApplyColour()
    {
        if (!CommandValues.TryParseColor(ColourText, out string colour, out _))
        {
            return Task.CompletedTask;
        }

        return Restyle(style => style with { Color = colour });
    }

    /// <summary>Takes every span style off the selection, so it is the title's own look again.</summary>
    [RelayCommand]
    private Task ClearStyle() => Restyle(_ => TitleStyle.Plain);

    [RelayCommand]
    private void SetAlign(string align) => Align = align;

    [RelayCommand]
    private void SetVAlign(string align) => VAlign = align;

    partial void OnFontChanged(FontChoice? value)
    {
        if (!_loading && value is not null)
        {
            _ = _run(new SetTitleStyleCommand(ClipId, Font: value.Family));
        }
    }

    partial void OnWeightChanged(string value) => Style(new SetTitleStyleCommand(ClipId, Weight: value));

    partial void OnItalicChanged(bool value) => Style(new SetTitleStyleCommand(ClipId, Italic: value));

    partial void OnAlignChanged(string value) => Style(new SetTitleStyleCommand(ClipId, Align: value));

    partial void OnVAlignChanged(string value) => Style(new SetTitleStyleCommand(ClipId, VAlign: value));

    partial void OnAnimationInChanged(string value) => Animate(new SetTitleAnimationCommand(ClipId, In: value));

    partial void OnAnimationOutChanged(string value) => Animate(new SetTitleAnimationCommand(ClipId, Out: value));

    partial void OnInSecondsChanged(double value) => Animate(new SetTitleAnimationCommand(ClipId, InDuration: Seconds(value)));

    partial void OnOutSecondsChanged(double value) => Animate(new SetTitleAnimationCommand(ClipId, OutDuration: Seconds(value)));

    private static Flicks Seconds(double value) => Flicks.FromSeconds(Math.Max(0.01, value));

    private void Style(SetTitleStyleCommand command)
    {
        if (!_loading)
        {
            _ = _run(command);
        }
    }

    private void Animate(SetTitleAnimationCommand command)
    {
        if (!_loading)
        {
            _ = _run(command);
        }
    }

    private Task Toggle(Func<TitleStyle, bool> has, Func<TitleStyle, bool, TitleStyle> set)
    {
        bool on = !SelectionIs(has);
        return Restyle(style => set(style, on));
    }

    private Task Restyle(Func<TitleStyle, TitleStyle> change)
    {
        if (IsTextAnimated || SelectionLength <= 0)
        {
            return Task.CompletedTask;
        }

        return CommitTextAsync(TitleMarkup.Restyle(Markup, SelectionStart, SelectionLength, change));
    }
}
