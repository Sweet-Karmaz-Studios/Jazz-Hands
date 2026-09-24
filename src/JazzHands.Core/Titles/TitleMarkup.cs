using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;

namespace JazzHands.Core.Titles;

/// <summary>What markup changes about a stretch of a title's text, over the title's own style.</summary>
/// <param name="Bold">Heavier than the title's weight, by three steps (regular to bold, bold to black).</param>
/// <param name="Italic">Slanted.</param>
/// <param name="Underline">Underlined.</param>
/// <param name="Color">A fill colour as sRGB hex, or null for the title's.</param>
/// <param name="Size">A text height in sequence pixels, or null for the title's.</param>
/// <param name="Font">A font family, or null for the title's.</param>
public sealed record TitleStyle(
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    string? Color = null,
    float? Size = null,
    string? Font = null)
{
    /// <summary>No change from the title's own style.</summary>
    public static TitleStyle Plain { get; } = new();

    /// <summary>True when this changes nothing.</summary>
    public bool IsPlain => this == Plain;
}

/// <summary>A stretch of a title's plain text with one style.</summary>
/// <param name="Start">Where it starts in <see cref="TitleText.Plain"/>, in UTF-16 units.</param>
/// <param name="Length">How long it is.</param>
/// <param name="Style">What the markup changes over it.</param>
public sealed record TitleSpan(int Start, int Length, TitleStyle Style)
{
    /// <summary>The first position after it.</summary>
    public int End => Start + Length;
}

/// <summary>A title's text read out of its markup: the characters, and their styles in order.</summary>
/// <param name="Plain">The text as it is drawn, tags and escapes gone.</param>
/// <param name="Spans">Styled stretches covering <paramref name="Plain"/> end to end, none empty.</param>
public sealed record TitleText(string Plain, EquatableArray<TitleSpan> Spans)
{
    /// <summary>The style at a position, plain past the end.</summary>
    public TitleStyle StyleAt(int index)
    {
        foreach (TitleSpan span in Spans)
        {
            if (index >= span.Start && index < span.End)
            {
                return span.Style;
            }
        }

        return TitleStyle.Plain;
    }
}

/// <summary>
/// The markup a title's <c>text</c> parameter is written in: plain text with a few square
/// bracket tags for the parts styled differently.
/// </summary>
/// <remarks>
/// <para>
/// <c>[b]</c> bold, <c>[i]</c> italic, <c>[u]</c> underline, <c>[color=#FFCC00]</c>,
/// <c>[size=48]</c> (sequence pixels) and <c>[font=Bahnschrift]</c>, each closed by its
/// <c>[/b]</c>, <c>[/color]</c> and so on. <c>\n</c> is a new line (a real line break is one too),
/// <c>\[</c> a bracket and <c>\\</c> a backslash. Tags are case blind, may nest, and an unclosed
/// one runs to the end.
/// </para>
/// <para>
/// Text rather than a structure in the project file so a title stays one readable, held
/// parameter that <c>param.set</c>, keyframes, presets and the command line all reach, as a
/// curve's points are (decision 197). Nothing a person could type is an error: a bracket that is
/// not one of these tags, or a closing tag with nothing open, is kept as the text it is.
/// </para>
/// </remarks>
public static class TitleMarkup
{
    private enum Kind
    {
        Bold,
        Italic,
        Underline,
        Color,
        Size,
        Font,
    }

    /// <summary>Reads markup into its text and styles.</summary>
    public static TitleText Parse(string? markup)
    {
        if (string.IsNullOrEmpty(markup))
        {
            return new TitleText(string.Empty, []);
        }

        var plain = new StringBuilder(markup.Length);
        var spans = ImmutableArray.CreateBuilder<TitleSpan>();
        var stacks = new Dictionary<Kind, List<string>>();
        TitleStyle current = TitleStyle.Plain;
        int spanStart = 0;

        void Restyle()
        {
            TitleStyle next = StyleOf(stacks);
            if (next == current)
            {
                return;
            }

            Close(plain.Length);
            current = next;
        }

        void Close(int end)
        {
            if (end > spanStart)
            {
                if (spans.Count > 0 && spans[^1].Style == current && spans[^1].End == spanStart)
                {
                    spans[^1] = spans[^1] with { Length = end - spans[^1].Start };
                }
                else
                {
                    spans.Add(new TitleSpan(spanStart, end - spanStart, current));
                }
            }

            spanStart = end;
        }

        for (int index = 0; index < markup.Length; index++)
        {
            char c = markup[index];
            if (c == '\\' && index + 1 < markup.Length)
            {
                char next = markup[index + 1];
                if (next is '[' or '\\')
                {
                    plain.Append(next);
                    index++;
                    continue;
                }

                if (next == 'n')
                {
                    plain.Append('\n');
                    index++;
                    continue;
                }
            }

            if (c == '[' && TryTag(markup, index, out Kind kind, out bool closing, out string value, out int length))
            {
                if (closing)
                {
                    if (!stacks.TryGetValue(kind, out List<string>? open) || open.Count == 0)
                    {
                        plain.Append(markup, index, length);
                        index += length - 1;
                        continue;
                    }

                    open.RemoveAt(open.Count - 1);
                }
                else
                {
                    if (!stacks.TryGetValue(kind, out List<string>? open))
                    {
                        open = [];
                        stacks[kind] = open;
                    }

                    open.Add(value);
                }

                index += length - 1;
                Restyle();
                continue;
            }

            // Windows line ends come in from a text box; a title has one kind of line break.
            if (c == '\r')
            {
                if (index + 1 < markup.Length && markup[index + 1] == '\n')
                {
                    continue;
                }

                c = '\n';
            }

            plain.Append(c);
        }

        Close(plain.Length);
        return new TitleText(plain.ToString(), new EquatableArray<TitleSpan>(spans.ToImmutable()));
    }

    /// <summary>The text with every tag and escape taken out.</summary>
    public static string PlainText(string? markup) => Parse(markup).Plain;

    /// <summary>Plain text as markup: brackets and backslashes escaped, nothing styled.</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (c is '[' or '\\')
            {
                result.Append('\\');
            }

            result.Append(c);
        }

        return result.ToString();
    }

    /// <summary>
    /// Writes text and its styles back as markup, with as few tags as keep the styles: the
    /// inverse of <see cref="Parse"/>, which the editor's spans editor saves through.
    /// </summary>
    public static string Format(TitleText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var result = new StringBuilder(text.Plain.Length + 16);
        var open = new List<(Kind Kind, string Value)>();

        foreach (TitleSpan span in Pieces(text))
        {
            List<(Kind Kind, string Value)> wanted = Tags(span.Style);

            // Keep the longest run of open tags the next piece also wants, in the same order.
            int keep = 0;
            while (keep < open.Count && keep < wanted.Count && open[keep] == wanted[keep])
            {
                keep++;
            }

            for (int index = open.Count - 1; index >= keep; index--)
            {
                result.Append("[/").Append(Name(open[index].Kind)).Append(']');
            }

            open.RemoveRange(keep, open.Count - keep);
            for (int index = keep; index < wanted.Count; index++)
            {
                (Kind kind, string value) = wanted[index];
                result.Append('[').Append(Name(kind));
                if (value.Length > 0)
                {
                    result.Append('=').Append(value);
                }

                result.Append(']');
                open.Add(wanted[index]);
            }

            result.Append(Escape(text.Plain.Substring(span.Start, span.Length)));
        }

        for (int index = open.Count - 1; index >= 0; index--)
        {
            result.Append("[/").Append(Name(open[index].Kind)).Append(']');
        }

        return result.ToString();
    }

    /// <summary>
    /// Markup for the same text with one style change over a stretch of the plain text: what the
    /// editor's bold, italic and colour buttons send.
    /// </summary>
    /// <param name="markup">The title's markup now.</param>
    /// <param name="start">Where the selection starts in the plain text.</param>
    /// <param name="length">How long it is.</param>
    /// <param name="change">What to do to each stretch's style inside the selection.</param>
    public static string Restyle(string? markup, int start, int length, Func<TitleStyle, TitleStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        TitleText text = Parse(markup);
        start = Math.Clamp(start, 0, text.Plain.Length);
        int end = Math.Clamp(start + Math.Max(0, length), start, text.Plain.Length);
        if (end == start)
        {
            return markup ?? string.Empty;
        }

        var spans = ImmutableArray.CreateBuilder<TitleSpan>();
        foreach (TitleSpan span in Pieces(text))
        {
            // Split each piece where the selection starts and ends, and change the inside.
            int[] cuts = [span.Start, Math.Clamp(start, span.Start, span.End), Math.Clamp(end, span.Start, span.End), span.End];
            for (int index = 0; index < 3; index++)
            {
                if (cuts[index + 1] > cuts[index])
                {
                    TitleStyle style = index == 1 ? change(span.Style) : span.Style;
                    spans.Add(new TitleSpan(cuts[index], cuts[index + 1] - cuts[index], style));
                }
            }
        }

        return Format(new TitleText(text.Plain, new EquatableArray<TitleSpan>(spans.ToImmutable())));
    }

    /// <summary>
    /// Markup with every <c>[size=n]</c> multiplied, for a preset's sample text scaled from the
    /// 1080 lines it was written for to the sequence's.
    /// </summary>
    public static string ScaleSizes(string? markup, float factor)
    {
        if (string.IsNullOrEmpty(markup) || MathF.Abs(factor - 1.0f) < 1e-6f)
        {
            return markup ?? string.Empty;
        }

        TitleText text = Parse(markup);
        if (text.Spans.All(span => span.Style.Size is null))
        {
            return markup;
        }

        return Format(text with
        {
            Spans = [.. text.Spans.Select(span => span.Style.Size is { } size ? span with { Style = span.Style with { Size = size * factor } } : span)],
        });
    }

    /// <summary>The spans with any gap between them filled in plain, so every character is in one.</summary>
    private static IEnumerable<TitleSpan> Pieces(TitleText text)
    {
        int at = 0;
        foreach (TitleSpan span in text.Spans.Where(span => span.Length > 0).OrderBy(span => span.Start))
        {
            int start = Math.Max(span.Start, at);
            int end = Math.Min(span.End, text.Plain.Length);
            if (start > at)
            {
                yield return new TitleSpan(at, start - at, TitleStyle.Plain);
            }

            if (end > start)
            {
                yield return new TitleSpan(start, end - start, span.Style);
                at = end;
            }
        }

        if (at < text.Plain.Length)
        {
            yield return new TitleSpan(at, text.Plain.Length - at, TitleStyle.Plain);
        }
    }

    private static List<(Kind Kind, string Value)> Tags(TitleStyle style)
    {
        var tags = new List<(Kind, string)>();
        if (style.Font is { Length: > 0 } font)
        {
            tags.Add((Kind.Font, font));
        }

        if (style.Size is { } size)
        {
            tags.Add((Kind.Size, size.ToString("0.##", CultureInfo.InvariantCulture)));
        }

        if (style.Color is { Length: > 0 } color)
        {
            tags.Add((Kind.Color, color));
        }

        if (style.Bold)
        {
            tags.Add((Kind.Bold, string.Empty));
        }

        if (style.Italic)
        {
            tags.Add((Kind.Italic, string.Empty));
        }

        if (style.Underline)
        {
            tags.Add((Kind.Underline, string.Empty));
        }

        return tags;
    }

    private static TitleStyle StyleOf(Dictionary<Kind, List<string>> stacks)
    {
        string? Top(Kind kind) => stacks.TryGetValue(kind, out List<string>? open) && open.Count > 0 ? open[^1] : null;

        return new TitleStyle(
            Bold: Top(Kind.Bold) is not null,
            Italic: Top(Kind.Italic) is not null,
            Underline: Top(Kind.Underline) is not null,
            Color: Top(Kind.Color),
            Size: Top(Kind.Size) is { } size ? float.Parse(size, NumberStyles.Float, CultureInfo.InvariantCulture) : null,
            Font: Top(Kind.Font));
    }

    private static string Name(Kind kind) => kind switch
    {
        Kind.Bold => "b",
        Kind.Italic => "i",
        Kind.Underline => "u",
        Kind.Color => "color",
        Kind.Size => "size",
        _ => "font",
    };

    /// <summary>Reads one tag at a bracket, if it is one this markup knows and its value makes sense.</summary>
    private static bool TryTag(string markup, int at, out Kind kind, out bool closing, out string value, out int length)
    {
        kind = Kind.Bold;
        closing = false;
        value = string.Empty;
        length = 0;

        int end = markup.IndexOf(']', at + 1);
        if (end < 0 || end - at > 80)
        {
            return false;
        }

        string inside = markup.Substring(at + 1, end - at - 1);
        length = end - at + 1;
        closing = inside.StartsWith('/');
        if (closing)
        {
            inside = inside[1..];
        }

        int equals = inside.IndexOf('=', StringComparison.Ordinal);
        string name = (equals < 0 ? inside : inside[..equals]).Trim().ToLowerInvariant();
        value = equals < 0 ? string.Empty : inside[(equals + 1)..].Trim();

        switch (name)
        {
            case "b":
                kind = Kind.Bold;
                return equals < 0;
            case "i":
                kind = Kind.Italic;
                return equals < 0;
            case "u":
                kind = Kind.Underline;
                return equals < 0;
            case "color" or "colour":
                kind = Kind.Color;
                if (closing)
                {
                    return equals < 0;
                }

                if (!ParamValues.TryParseColor(value, out _))
                {
                    return false;
                }

                value = value.ToUpperInvariant();
                return true;
            case "size":
                kind = Kind.Size;
                if (closing)
                {
                    return equals < 0;
                }

                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float size) || !float.IsFinite(size) || size <= 0 || size > 4000)
                {
                    return false;
                }

                value = size.ToString("0.##", CultureInfo.InvariantCulture);
                return true;
            case "font":
                kind = Kind.Font;
                return closing ? equals < 0 : value.Length > 0;
            default:
                return false;
        }
    }
}
