using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Titles;

/// <summary>The animations a title preset comes with, as a preset file writes them.</summary>
/// <param name="In">The animation in, or <c>none</c>.</param>
/// <param name="InDuration">How long it takes, as a time (<c>0.5s</c>).</param>
/// <param name="Out">The animation out.</param>
/// <param name="OutDuration">How long it takes.</param>
public sealed record TitlePresetAnimation(
    string In = TitleAnimations.None,
    string InDuration = "0.5s",
    string Out = TitleAnimations.None,
    string OutDuration = "0.5s");

/// <summary>
/// A title preset: a look, a place on the frame, sample text and animations, in a JSON file.
/// </summary>
/// <remarks>
/// <para>
/// A preset is the title's parameters as the command line types them (<c>"size": "72"</c>,
/// <c>"box": "#00000099"</c>), so a preset file reads like the <c>jazz title add</c> that would
/// make the same title, and <c>jazz effect list gen.title</c> documents every key. It is written
/// for a 1080 line frame: every distance (size, position, padding, outline, shadow) is scaled to
/// the sequence's height when used, so a lower third sits in the same place at 720p and at 4K. In
/// a portrait frame (Shorts, Reels) sizes follow the width, its short side, and the 16:9 frame the
/// preset was placed in is laid across the width and the middle 70% of the height, clear of the
/// buttons and captions those apps draw over the top and bottom.
/// </para>
/// <para>
/// Built-in presets ship inside the engine; a person's own are <c>.json</c> files in
/// <c>%APPDATA%\JazzHands\titles</c>, and one with a built-in's name replaces it.
/// </para>
/// </remarks>
/// <param name="Name">The name <c>--preset</c> takes, in kebab case.</param>
/// <param name="Label">What the editor calls it.</param>
/// <param name="Description">One sentence on when to use it.</param>
/// <param name="Text">The text a title made from it gets when none is given, as markup.</param>
/// <param name="Duration">How long a title made from it lasts when nothing says, as a time.</param>
/// <param name="Animation">What it comes in and goes out with.</param>
/// <param name="Params">Parameter values by name, as command-line text, for a 1080 line frame.</param>
public sealed record TitlePreset(
    string Name,
    string Label = "",
    string Description = "",
    string Text = "Title",
    string Duration = "5s",
    TitlePresetAnimation? Animation = null,
    ImmutableSortedDictionary<string, string>? Params = null)
{
    /// <summary>The frame height presets are written for.</summary>
    public const float ReferenceHeight = 1080.0f;

    /// <summary>The frame width presets are written for.</summary>
    public const float ReferenceWidth = 1920.0f;

    /// <summary>How much of a portrait frame's height a preset's frame is laid across, clear of the apps' buttons.</summary>
    public const float PortraitBand = 0.7f;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>True for a preset that ships with the editor.</summary>
    [JsonIgnore]
    public bool BuiltIn { get; init; }

    /// <summary>The file it was read from, or empty for a built-in.</summary>
    [JsonIgnore]
    public string Source { get; init; } = string.Empty;

    /// <summary>The parameter values, never null.</summary>
    [JsonIgnore]
    public ImmutableSortedDictionary<string, string> Values => Params ?? ImmutableSortedDictionary<string, string>.Empty;

    /// <summary>The animations, never null.</summary>
    [JsonIgnore]
    public TitlePresetAnimation Animations => Animation ?? new TitlePresetAnimation();

    /// <summary>Reads a preset file, or says why it is not one.</summary>
    /// <param name="json">The file's text.</param>
    /// <param name="title">The title generator, whose parameters the values must be.</param>
    /// <param name="preset">The preset.</param>
    /// <param name="error">Why it is not one.</param>
    public static bool TryRead(string json, EffectDescriptor title, out TitlePreset? preset, out string? error)
    {
        ArgumentNullException.ThrowIfNull(title);
        preset = null;

        try
        {
            preset = JsonSerializer.Deserialize<TitlePreset>(json, Json);
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }

        if (preset is null || string.IsNullOrWhiteSpace(preset.Name))
        {
            error = "A title preset needs a name.";
            preset = null;
            return false;
        }

        error = Check(preset, title);
        if (error is not null)
        {
            preset = null;
            return false;
        }

        return true;
    }

    /// <summary>The preset as a file's text.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>What is wrong with a preset, or null when nothing is.</summary>
    public static string? Check(TitlePreset preset, EffectDescriptor title)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(title);

        foreach ((string name, string text) in preset.Values)
        {
            if (title.Param(name) is not { } parameter)
            {
                return $"'{preset.Name}' sets '{name}', which a title does not have.";
            }

            if (!ParamValues.TryParse(parameter, text, out _, out string? error))
            {
                return $"'{preset.Name}' sets '{name}' to '{text}': {error}";
            }
        }

        TitlePresetAnimation animation = preset.Animations;
        foreach (string name in new[] { animation.In, animation.Out })
        {
            if (!TitleAnimations.IsKnown(name))
            {
                return $"'{preset.Name}' animates with '{name}', which is not one of {string.Join(", ", TitleAnimations.Names)}.";
            }
        }

        foreach (string time in new[] { preset.Duration, animation.InDuration, animation.OutDuration })
        {
            if (!Timecode.TryParse(time, Rational.Fps30, out Flicks value) || value <= Flicks.Zero)
            {
                return $"'{preset.Name}' has '{time}' for a duration, which is not a time greater than zero.";
            }
        }

        return null;
    }

    /// <summary>
    /// A title's own parameters with this preset's values set, scaled to the frame, over what
    /// was there. Animation and text are left to the caller.
    /// </summary>
    public Effect Style(Effect own, EffectDescriptor title, Vector2 frame)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(title);

        float scale = SizeScale(frame);
        Effect result = own;
        foreach ((string name, string text) in Values)
        {
            if (title.Param(name) is { } parameter)
            {
                ParamValue value = name == TitleParams.Position && frame.X < frame.Y && ParamValues.Parse(parameter, text) is ParamValue.Float2 place
                    ? new ParamValue.Float2(place.Value * new Vector2(frame.X / ReferenceWidth, frame.Y * PortraitBand / ReferenceHeight))
                    : Scale(name, ParamValues.Parse(parameter, text), name == TitleParams.Width && frame.X < frame.Y ? frame.X / ReferenceWidth : scale);
                result = result.WithParameter(name, AnimatedValue.Constant(value));
            }
        }

        return result;
    }

    /// <summary>How much a preset's sizes grow for a frame: by its short side, the height of a landscape frame and the width of a portrait one.</summary>
    public static float SizeScale(Vector2 frame) => MathF.Min(frame.X, frame.Y) / ReferenceHeight;

    /// <summary>How long a title from this preset lasts when nothing says.</summary>
    [JsonIgnore]
    public Flicks DefaultLength => Timecode.TryParse(Duration, Rational.Fps30, out Flicks value) && value > Flicks.Zero ? value : Flicks.FromSeconds(5);

    /// <summary>The preset's animations as times.</summary>
    public TitleAnimation AnimationFor() => new(
        Animations.In,
        Timecode.TryParse(Animations.InDuration, Rational.Fps30, out Flicks inLength) ? inLength : TitleAnimations.DefaultDuration,
        Animations.Out,
        Timecode.TryParse(Animations.OutDuration, Rational.Fps30, out Flicks outLength) ? outLength : TitleAnimations.DefaultDuration);

    /// <summary>A value measured in sequence pixels scaled from the reference frame to another; anything else as it is.</summary>
    public static ParamValue Scale(string name, ParamValue value, float scale)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!TitleParams.Distances.Contains(name) || MathF.Abs(scale - 1.0f) < 1e-6f)
        {
            return value;
        }

        return value switch
        {
            ParamValue.Float number => new ParamValue.Float(number.Value * scale),
            ParamValue.Float2 pair => new ParamValue.Float2(pair.Value * scale),
            _ => value,
        };
    }
}
