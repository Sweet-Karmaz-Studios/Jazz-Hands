using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Titles;

/// <summary>The parameter names of the title generator that Core works with.</summary>
public static class TitleParams
{
    /// <summary>The title generator's type.</summary>
    public const string GeneratorId = "gen.title";

    /// <summary>The markup.</summary>
    public const string Text = "text";

    /// <summary>The font family.</summary>
    public const string Font = "font";

    /// <summary>How heavy the letters are.</summary>
    public const string Weight = "weight";

    /// <summary>Slanted or upright.</summary>
    public const string Italic = "italic";

    /// <summary>Text height in sequence pixels.</summary>
    public const string Size = "size";

    /// <summary>The fill.</summary>
    public const string Colour = "colour";

    /// <summary>left, centre or right.</summary>
    public const string Align = "align";

    /// <summary>top, middle or bottom.</summary>
    public const string VAlign = "valign";

    /// <summary>Where the text block's anchor sits, from the frame centre.</summary>
    public const string Position = "position";

    /// <summary>The width lines wrap at; 0 for no wrapping.</summary>
    public const string Width = "width";

    /// <summary>Line height, as a multiple of the font's.</summary>
    public const string LineSpacing = "line-spacing";

    /// <summary>Extra space between letters.</summary>
    public const string Tracking = "tracking";

    /// <summary>The outline's colour.</summary>
    public const string Stroke = "stroke";

    /// <summary>The outline's width.</summary>
    public const string StrokeWidth = "stroke-width";

    /// <summary>The box's colour.</summary>
    public const string Box = "box";

    /// <summary>Space between the text and the box's edge.</summary>
    public const string BoxPadding = "box-padding";

    /// <summary>The box's corner radius.</summary>
    public const string BoxRadius = "box-radius";

    /// <summary>The shadow's colour.</summary>
    public const string Shadow = "shadow";

    /// <summary>How far the shadow falls.</summary>
    public const string ShadowOffset = "shadow-offset";

    /// <summary>How soft the shadow is.</summary>
    public const string ShadowBlur = "shadow-blur";

    /// <summary>Animation channel: how much of the text shows, 0 to 1.</summary>
    public const string Reveal = "reveal";

    /// <summary>Animation channel: what the reveal counts in.</summary>
    public const string RevealBy = "reveal-by";

    /// <summary>Animation channel: how many units fade in together.</summary>
    public const string RevealSoft = "reveal-soft";

    /// <summary>Animation channel: the title's own opacity.</summary>
    public const string Fade = "fade";

    /// <summary>Animation channel: a move away from the position.</summary>
    public const string Offset = "offset";

    /// <summary>Animation channel: a scale about the text block's centre.</summary>
    public const string Zoom = "zoom";

    /// <summary>Animation channel: a blur, in sequence pixels.</summary>
    public const string Blur = "blur";

    /// <summary>The animation the title comes in with, as it was last expanded.</summary>
    public const string AnimationIn = "anim-in";

    /// <summary>The animation the title goes out with.</summary>
    public const string AnimationOut = "anim-out";

    /// <summary>
    /// Every parameter measured in sequence pixels, which a preset written for a 1080 line frame
    /// scales to the sequence's height.
    /// </summary>
    public static ImmutableArray<string> Distances { get; } =
        [Size, Position, Width, Tracking, StrokeWidth, BoxPadding, BoxRadius, ShadowOffset, ShadowBlur, Offset, Blur];

    /// <summary>The channels the animation presets drive, which choosing another animation resets.</summary>
    public static ImmutableArray<string> Channels { get; } = [Reveal, RevealBy, RevealSoft, Fade, Offset, Zoom, Blur];
}

/// <summary>A title's animations as they were last applied.</summary>
/// <param name="In">The animation it comes in with, or <c>none</c>.</param>
/// <param name="InDuration">How long that takes.</param>
/// <param name="Out">The animation it goes out with, or <c>none</c>.</param>
/// <param name="OutDuration">How long that takes.</param>
public sealed record TitleAnimation(string In, Flicks InDuration, string Out, Flicks OutDuration)
{
    /// <summary>No animation either way.</summary>
    public static TitleAnimation None { get; } = new(TitleAnimations.None, TitleAnimations.DefaultDuration, TitleAnimations.None, TitleAnimations.DefaultDuration);
}

/// <summary>
/// The title animation presets, and how each becomes keyframes.
/// </summary>
/// <remarks>
/// <para>
/// An animation is not stored as a name the renderer interprets: it is expanded into keyframes on
/// the title's animation channels (<c>fade</c>, <c>offset</c>, <c>zoom</c>, <c>blur</c> and
/// <c>reveal</c>) when it is chosen, so the curve editor, <c>keyframe.*</c> and a hand edit can
/// change what it does. The names are kept too (<c>anim-in</c>, <c>anim-out</c>), so the Inspector
/// can show what was chosen and a new choice knows what to replace. The channels are the title's
/// own rather than the clip's transform and opacity, which stay free for the person: a title can
/// be moved with <c>clip.set-transform</c> and still slide in (decision 198).
/// </para>
/// <para>
/// The in animation runs from the clip's start, the out one up to its end. Where the two would
/// overlap, both are shortened in proportion.
/// </para>
/// </remarks>
public static class TitleAnimations
{
    /// <summary>No animation.</summary>
    public const string None = "none";

    /// <summary>How long an animation takes when nothing says.</summary>
    public static readonly Flicks DefaultDuration = Flicks.FromSeconds(0.5);

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        [None] = "No animation.",
        ["fade"] = "Fades in from nothing, or out to nothing.",
        ["slide-left"] = "Slides leftwards a tenth of the frame while it fades.",
        ["slide-right"] = "Slides rightwards a tenth of the frame while it fades.",
        ["slide-up"] = "Rises a tenth of the frame while it fades.",
        ["slide-down"] = "Drops a tenth of the frame while it fades.",
        ["scale"] = "Grows from 70% to full size while it fades in; shrinks as it fades out.",
        ["typewriter"] = "Types on a character at a time, or deletes itself backwards.",
        ["word-reveal"] = "Each word fades in after the one before, overlapping.",
        ["blur"] = "Comes into focus from a blur while it fades, or blurs away.",
        ["wipe"] = "A soft edge sweeps across the text from left to right.",
    };

    /// <summary>Every animation's name, <c>none</c> first.</summary>
    public static ImmutableArray<string> Names { get; } = [.. Descriptions.Keys];

    /// <summary>One sentence on what an animation does, or null for a name that is not one.</summary>
    public static string? Describe(string name) => Descriptions.GetValueOrDefault(name);

    /// <summary>True for a name that is an animation.</summary>
    public static bool IsKnown(string? name) => name is not null && Descriptions.ContainsKey(name);

    /// <summary>
    /// A title's own parameters with its animation channels replaced by these animations.
    /// </summary>
    /// <param name="own">The title's parameters now.</param>
    /// <param name="length">How long the clip is.</param>
    /// <param name="frame">The sequence's size, which the slides move a tenth of.</param>
    /// <param name="animation">What to come in and go out with.</param>
    public static Effect Apply(Effect own, Flicks length, Vector2 frame, TitleAnimation animation)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(animation);

        Effect result = own with
        {
            Parameters = new EquatableArray<EffectParameter>(own.Parameters.Where(parameter => !TitleParams.Channels.Contains(parameter.Name))),
        };

        string inName = IsKnown(animation.In) ? animation.In : None;
        string outName = IsKnown(animation.Out) ? animation.Out : None;
        result = result
            .WithParameter(TitleParams.AnimationIn, AnimatedValue.Constant(new ParamValue.Enum(inName)))
            .WithParameter(TitleParams.AnimationOut, AnimatedValue.Constant(new ParamValue.Enum(outName)));

        (Flicks inLength, Flicks outLength) = Fit(
            inName == None ? Flicks.Zero : Positive(animation.InDuration),
            outName == None ? Flicks.Zero : Positive(animation.OutDuration),
            length);

        var channels = new Dictionary<string, List<Keyframe>>(StringComparer.Ordinal);
        void Key(string channel, Flicks time, ParamValue value, Interp interp)
        {
            if (!channels.TryGetValue(channel, out List<Keyframe>? keys))
            {
                keys = [];
                channels[channel] = keys;
            }

            keys.Add(new Keyframe(time, value, interp));
        }

        foreach (Move move in Moves(inName, frame))
        {
            Key(move.Channel, Flicks.Zero, move.Away, move.IsSetting ? Interp.Hold : Interp.EaseOut);
            if (!move.IsSetting)
            {
                Key(move.Channel, inLength, move.Rest, Interp.Linear);
            }
        }

        foreach (Move move in Moves(outName, frame))
        {
            Flicks start = length - outLength;
            Key(move.Channel, start, move.IsSetting ? move.Away : move.Rest, move.IsSetting ? Interp.Hold : Interp.EaseIn);
            if (!move.IsSetting)
            {
                Key(move.Channel, length, move.Away, Interp.Linear);
            }
        }

        foreach ((string channel, List<Keyframe> keys) in channels)
        {
            // A setting both animations make the same, or only one makes, is a constant; so is a
            // channel with nothing but equal values.
            AnimatedValue value = keys.All(key => key.Value == keys[0].Value)
                ? AnimatedValue.Constant(keys[0].Value)
                : new KeyframedValue(Distinct(keys));
            result = result.WithParameter(channel, value);
        }

        return result;
    }

    /// <summary>
    /// The animations a title's parameters say were applied, with their durations read from the
    /// keyframes they made. Durations fall back to the default where the keyframes were edited
    /// out of recognition.
    /// </summary>
    public static TitleAnimation Read(Effect? own, Flicks length)
    {
        string inName = own?.Parameter(TitleParams.AnimationIn) is StaticValue { Value: ParamValue.Enum { Value: var chosenIn } } && IsKnown(chosenIn) ? chosenIn : None;
        string outName = own?.Parameter(TitleParams.AnimationOut) is StaticValue { Value: ParamValue.Enum { Value: var chosenOut } } && IsKnown(chosenOut) ? chosenOut : None;

        Flicks inLength = DefaultDuration;
        Flicks outLength = DefaultDuration;
        if (inName != None && own?.Parameter(Primary(inName)) is KeyframedValue { Keyframes.Length: >= 2 } inKeys && inKeys.Keyframes[1].Time > Flicks.Zero)
        {
            inLength = inKeys.Keyframes[1].Time - inKeys.Keyframes[0].Time;
        }

        if (outName != None && own?.Parameter(Primary(outName)) is KeyframedValue { Keyframes.Length: >= 2 } outKeys)
        {
            Flicks end = outKeys.Keyframes[^1].Time;
            Flicks start = outKeys.Keyframes[^2].Time;
            if (end > start)
            {
                outLength = end - start;
            }
        }

        return new TitleAnimation(inName, inLength, outName, outLength);
    }

    /// <summary>
    /// A title's parameters after its clip changed length: the out animation's keyframes move
    /// with the clip's end, so a trimmed title still leaves as it did. Everything else stays.
    /// </summary>
    public static Effect Retime(Effect own, Flicks before, Flicks after)
    {
        ArgumentNullException.ThrowIfNull(own);

        TitleAnimation animation = Read(own, before);
        if (animation.Out == None || before == after)
        {
            return own;
        }

        Flicks shift = after - before;
        Flicks from = before - animation.OutDuration;
        Effect result = own;
        foreach (EffectParameter parameter in own.Parameters)
        {
            if (!TitleParams.Channels.Contains(parameter.Name) || parameter.Value is not KeyframedValue keyed)
            {
                continue;
            }

            var moved = keyed.Keyframes
                .Select(key => key.Time >= from ? key with { Time = Max(Flicks.Zero, key.Time + shift) } : key)
                .ToList();
            result = result.WithParameter(parameter.Name, new KeyframedValue(Distinct(moved)));
        }

        return result;
    }

    /// <summary>The channel whose keyframes say how long an animation lasts.</summary>
    private static string Primary(string name) => name switch
    {
        "typewriter" or "word-reveal" or "wipe" => TitleParams.Reveal,
        _ => TitleParams.Fade,
    };

    /// <summary>
    /// What an animation changes: each channel's value when the title is away (before it is in,
    /// after it is out) and when it is at rest. A setting is a value held for the animation's
    /// length rather than moved, such as what a reveal counts in.
    /// </summary>
    private static IEnumerable<Move> Moves(string name, Vector2 frame)
    {
        ParamValue shown = new ParamValue.Float(1);
        ParamValue hidden = new ParamValue.Float(0);
        var still = new ParamValue.Float2(Vector2.Zero);
        float across = frame.X / 10.0f;
        float down = frame.Y / 10.0f;

        switch (name)
        {
            case "fade":
                yield return new Move(TitleParams.Fade, hidden, shown);
                break;
            case "slide-left":
                yield return new Move(TitleParams.Offset, new ParamValue.Float2(across, 0), still);
                yield return new Move(TitleParams.Fade, hidden, shown);
                break;
            case "slide-right":
                yield return new Move(TitleParams.Offset, new ParamValue.Float2(-across, 0), still);
                yield return new Move(TitleParams.Fade, hidden, shown);
                break;
            case "slide-up":
                yield return new Move(TitleParams.Offset, new ParamValue.Float2(0, down), still);
                yield return new Move(TitleParams.Fade, hidden, shown);
                break;
            case "slide-down":
                yield return new Move(TitleParams.Offset, new ParamValue.Float2(0, -down), still);
                yield return new Move(TitleParams.Fade, hidden, shown);
                break;
            case "scale":
                yield return new Move(TitleParams.Zoom, new ParamValue.Float(0.7f), shown);
                yield return new Move(TitleParams.Fade, hidden, shown);
                break;
            case "blur":
                yield return new Move(TitleParams.Blur, new ParamValue.Float(frame.Y / 36.0f), hidden);
                yield return new Move(TitleParams.Fade, hidden, shown);
                break;
            case "typewriter":
                yield return new Move(TitleParams.Reveal, hidden, shown);
                yield return Setting(TitleParams.RevealBy, new ParamValue.Enum("characters"));
                yield return Setting(TitleParams.RevealSoft, new ParamValue.Float(0));
                break;
            case "word-reveal":
                yield return new Move(TitleParams.Reveal, hidden, shown);
                yield return Setting(TitleParams.RevealBy, new ParamValue.Enum("words"));
                yield return Setting(TitleParams.RevealSoft, new ParamValue.Float(2));
                break;
            case "wipe":
                yield return new Move(TitleParams.Reveal, hidden, shown);
                yield return Setting(TitleParams.RevealBy, new ParamValue.Enum("wipe"));
                yield return Setting(TitleParams.RevealSoft, new ParamValue.Float(2));
                break;
        }
    }

    private static Move Setting(string channel, ParamValue value) => new(channel, value, value, IsSetting: true);

    /// <summary>Both animations shortened in proportion when together they are longer than the clip.</summary>
    private static (Flicks In, Flicks Out) Fit(Flicks inLength, Flicks outLength, Flicks length)
    {
        Flicks total = inLength + outLength;
        if (total <= length || total <= Flicks.Zero)
        {
            return (inLength, outLength);
        }

        var scaledIn = new Flicks((long)((Int128)inLength.Value * length.Value / total.Value));
        return (scaledIn, length - scaledIn);
    }

    private static Flicks Positive(Flicks duration) => duration > Flicks.Zero ? duration : DefaultDuration;

    private static Flicks Max(Flicks a, Flicks b) => a > b ? a : b;

    /// <summary>Keyframes with one per time, the later kept, so an in ending where the out starts does not stack two.</summary>
    private static List<Keyframe> Distinct(List<Keyframe> keys) =>
        [.. keys.GroupBy(key => key.Time.Value).Select(group => group.Last()).OrderBy(key => key.Time.Value)];

    private readonly record struct Move(string Channel, ParamValue Away, ParamValue Rest, bool IsSetting = false);
}
