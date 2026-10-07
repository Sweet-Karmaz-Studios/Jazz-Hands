using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Core.Titles;

namespace JazzHands.Core.Subtitles;

/// <summary>
/// How cues are drawn: as titles, with the track's style turned into the title generator's
/// parameters at the frame's size.
/// </summary>
/// <remarks>
/// <para>
/// A subtitle is a title that knows where it goes: text in the track's font and colours, placed
/// by its alignment inside a margin, wrapping at the frame's width less the margins. Burning in
/// is then the title renderer's job, with no text drawing of its own, and a burned-in cue looks
/// as a title set the same way would.
/// </para>
/// <para>
/// Cues showing at once in the same place stack: they are drawn as one title, earliest first, a
/// line each, so the stack grows upwards from a bottom cue and downwards from a top one.
/// </para>
/// </remarks>
public static class SubtitleLook
{
    /// <summary>The cues of a subtitle track showing at a time, in start order.</summary>
    public static ImmutableArray<Clip> CuesAt(Track track, Flicks time)
    {
        ArgumentNullException.ThrowIfNull(track);
        return [.. track.Clips.Where(clip => clip.Enabled && clip.Cue is not null && clip.Start <= time && time < clip.End)];
    }

    /// <summary>
    /// The titles to draw for the cues showing at a time: one per place any of them sits, with
    /// the cue whose times it takes from (the earliest).
    /// </summary>
    public static ImmutableArray<(Clip First, Effect Title)> Titles(Track track, Flicks time, int frameWidth, int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(track);
        SubtitleStyle style = track.SubtitleStyle ?? SubtitleStyle.Default;
        ImmutableArray<Clip> showing = CuesAt(track, time);

        // A cue whose ASS line places, fades or sizes it is drawn on its own, as it says.
        var placed = showing.Select(clip => (Clip: clip, Overrides: AssOverrides.Read(clip.Cue!.Raw))).ToList();
        return [
            .. placed.Where(cue => cue.Overrides is null)
                .Select(cue => cue.Clip)
                .GroupBy(clip => clip.Cue!.Align)
                .Select(group => (group.First(), Title(style, string.Join("\n", group.Select(clip => clip.Cue!.Text)), group.Key, frameWidth, frameHeight))),
            .. placed.Where(cue => cue.Overrides is not null)
                .Select(cue => (cue.Clip, Title(style, cue.Clip, cue.Overrides!, frameWidth, frameHeight))),
        ];
    }

    /// <summary>
    /// A cue drawn with its ASS overrides: its size, outline and shadow in place of the track's,
    /// its anchor where <c>\pos</c> puts it, and its opacity fading as <c>\fad</c> says, in the
    /// cue's own time.
    /// </summary>
    public static Effect Title(SubtitleStyle style, Clip cue, AssOverrides overrides, int frameWidth, int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(cue);
        ArgumentNullException.ThrowIfNull(overrides);

        double script = AssOverrides.ScriptSize.Y;
        SubtitleStyle own = style with
        {
            Size = overrides.Size is { } size ? size / script : style.Size,
            OutlineWidth = overrides.Border is { } border ? border / script : style.OutlineWidth,
            Shadow = overrides.Shadow is 0.0 ? "#00000000" : style.Shadow,
        };

        Effect title = Title(own, cue.Cue!.Text, cue.Cue.Align, frameWidth, frameHeight);
        if (overrides.Position is { } at)
        {
            // Script pixels from the top left, to the frame's from its middle.
            var position = new Vector2(
                (at.X / AssOverrides.ScriptSize.X * frameWidth) - (frameWidth / 2f),
                (at.Y / AssOverrides.ScriptSize.Y * frameHeight) - (frameHeight / 2f));
            title = title.WithParameter(TitleParams.Position, AnimatedValue.Constant(new ParamValue.Float2(position)));
        }

        if (overrides.Shadow is { } offset and > 0)
        {
            float pixels = (float)(offset / script * frameHeight);
            title = title.WithParameter(TitleParams.ShadowOffset, AnimatedValue.Constant(new ParamValue.Float2(new Vector2(pixels))));
        }

        if (overrides.FadeIn > 0 || overrides.FadeOut > 0)
        {
            Flicks length = cue.Duration;
            Flicks fadeIn = Flicks.Min(Flicks.FromSeconds(overrides.FadeIn / 1000.0), length);
            Flicks fadeOut = Flicks.Max(fadeIn, length - Flicks.FromSeconds(overrides.FadeOut / 1000.0));
            var keys = new List<Keyframe>();
            if (fadeIn > Flicks.Zero)
            {
                keys.Add(new Keyframe(Flicks.Zero, new ParamValue.Float(0), Interp.Linear));
            }

            keys.Add(new Keyframe(fadeIn, new ParamValue.Float(1), Interp.Linear));
            if (fadeOut < length)
            {
                keys.Add(new Keyframe(fadeOut, new ParamValue.Float(1), Interp.Linear));
                keys.Add(new Keyframe(length, new ParamValue.Float(0), Interp.Linear));
            }

            title = title.WithParameter(TitleParams.Fade, new KeyframedValue(keys.DistinctBy(key => key.Time).ToList()));
        }

        return title;
    }

    /// <summary>The title generator's parameters that draw text in a subtitle style at a place on a frame.</summary>
    public static Effect Title(SubtitleStyle style, string text, SubtitleAlign align, int frameWidth, int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(text);

        float height = frameHeight;
        float size = (float)(style.Size * height);
        float margin = (float)(style.Margin * height);
        int number = (int)align;
        int column = (number - 1) % 3;
        int row = (number - 1) / 3;

        var position = new Vector2(
            column switch { 0 => -((frameWidth / 2f) - margin), 2 => (frameWidth / 2f) - margin, _ => 0f },
            row switch { 0 => (height / 2f) - margin, 2 => -((height / 2f) - margin), _ => 0f });

        Effect title = Effect.Create(TitleParams.GeneratorId);
        foreach ((string name, ParamValue value) in new (string, ParamValue)[]
        {
            (TitleParams.Text, new ParamValue.Text(text)),
            (TitleParams.Font, new ParamValue.Text(style.Font)),
            (TitleParams.Weight, new ParamValue.Enum(style.Weight)),
            (TitleParams.Italic, new ParamValue.Bool(style.Italic)),
            (TitleParams.Size, new ParamValue.Float(size)),
            (TitleParams.Colour, Colour(style.Color)),
            (TitleParams.Align, new ParamValue.Enum(column switch { 0 => "left", 2 => "right", _ => "centre" })),
            (TitleParams.VAlign, new ParamValue.Enum(row switch { 0 => "bottom", 2 => "top", _ => "middle" })),
            (TitleParams.Position, new ParamValue.Float2(position)),
            (TitleParams.Width, new ParamValue.Float(Math.Max(1f, frameWidth - (margin * 2)))),
            (TitleParams.Stroke, Colour(style.Outline)),
            (TitleParams.StrokeWidth, new ParamValue.Float((float)(style.OutlineWidth * height))),
            (TitleParams.Box, Colour(style.Box)),
            (TitleParams.BoxPadding, new ParamValue.Float(size * 0.3f)),
            (TitleParams.BoxRadius, new ParamValue.Float(size * 0.12f)),
            (TitleParams.Shadow, Colour(style.Shadow)),
            (TitleParams.ShadowOffset, new ParamValue.Float2(new Vector2(size * 0.05f))),
            (TitleParams.ShadowBlur, new ParamValue.Float(size * 0.12f)),
        })
        {
            title = title.WithParameter(name, AnimatedValue.Constant(value));
        }

        return title;
    }

    private static ParamValue.Color Colour(string hex) =>
        new(ParamValues.TryParseColor(hex, out Vector4 linear) ? linear : Vector4.Zero);
}
