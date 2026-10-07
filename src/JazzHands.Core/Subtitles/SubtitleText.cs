using System.Globalization;
using System.Text;
using JazzHands.Core.Model;
using JazzHands.Core.Titles;

namespace JazzHands.Core.Subtitles;

/// <summary>
/// Cue text between the HTML-like tags of SubRip and WebVTT and the title markup cues are kept in.
/// </summary>
/// <remarks>
/// Bold, italic, underline and a font colour cross over both ways: in SubRip as a font tag, in
/// WebVTT as a <c>&lt;c.class&gt;</c> span coloured by the file's style block or by WebVTT's
/// default colour classes. WebVTT's voices, ruby, inline timestamps and other classes, and any
/// tag this does not know, are dropped and their text kept;
/// SubRip, which has no escapes, keeps a bracket it does not know as text.
/// SubRip files often carry an ASS alignment tag, <c>{\an8}</c>, which is read as where the cue
/// sits; any other brace block is dropped.
/// </remarks>
public static class SubtitleText
{
    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = "#FFFFFF",
        ["black"] = "#000000",
        ["red"] = "#FF0000",
        ["lime"] = "#00FF00",
        ["green"] = "#008000",
        ["blue"] = "#0000FF",
        ["yellow"] = "#FFFF00",
        ["cyan"] = "#00FFFF",
        ["aqua"] = "#00FFFF",
        ["magenta"] = "#FF00FF",
        ["fuchsia"] = "#FF00FF",
        ["silver"] = "#C0C0C0",
        ["gray"] = "#808080",
        ["grey"] = "#808080",
        ["orange"] = "#FFA500",
    };

    /// <summary>WebVTT's default colour classes, which colour a <c>&lt;c&gt;</c> span with no style block.</summary>
    private static readonly Dictionary<string, string> VttDefault = new(StringComparer.Ordinal)
    {
        ["white"] = "#FFFFFF",
        ["lime"] = "#00FF00",
        ["cyan"] = "#00FFFF",
        ["red"] = "#FF0000",
        ["yellow"] = "#FFFF00",
        ["magenta"] = "#FF00FF",
        ["blue"] = "#0000FF",
        ["black"] = "#000000",
    };

    /// <summary>
    /// The WebVTT class a colour is written with: one of WebVTT's own names when it is one of
    /// their colours, otherwise <c>c</c> and the hex value.
    /// </summary>
    public static string VttClass(string colour)
    {
        ArgumentNullException.ThrowIfNull(colour);
        string hex = colour.Trim().TrimStart('#').ToUpperInvariant();
        return VttDefault.FirstOrDefault(pair => pair.Value[1..] == hex).Key ?? "c" + hex;
    }

    /// <summary>
    /// SubRip or WebVTT cue text as title markup, and the alignment an <c>{\anN}</c> tag gave it.
    /// </summary>
    /// <param name="classes">For WebVTT, the colour of each class the file's style block names.</param>
    /// <param name="text">The cue's lines, joined with line breaks.</param>
    /// <param name="entities">True for WebVTT, whose text escapes <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c>.</param>
    public static (string Markup, SubtitleAlign? Align) FromHtml(string text, bool entities, IReadOnlyDictionary<string, string>? classes = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var markup = new StringBuilder(text.Length + 8);
        var fonts = new Stack<bool>();
        SubtitleAlign? align = null;

        for (int at = 0; at < text.Length; at++)
        {
            char c = text[at];

            if (c == '{' && text.IndexOf('}', at) is int close and > 0 && at + 1 < text.Length && text[at + 1] == '\\')
            {
                string block = text[(at + 1)..close];
                if (AlignTag(block) is { } tagged)
                {
                    align = tagged;
                }

                at = close;
                continue;
            }

            if (c == '<' && text.IndexOf('>', at) is int end and > 0)
            {
                string tag = text[(at + 1)..end].Trim();
                if (Tag(tag, fonts, classes) is { } written)
                {
                    markup.Append(written);
                    at = end;
                    continue;
                }

                if (entities && IsTagLike(tag))
                {
                    at = end;
                    continue;
                }
            }

            if (entities && c == '&' && text.IndexOf(';', at) is int semicolon and > 0 && semicolon - at <= 8
                && Entity(text[(at + 1)..semicolon]) is { } decoded)
            {
                Append(markup, decoded);
                at = semicolon;
                continue;
            }

            Append(markup, c);
        }

        return (markup.ToString(), align);
    }

    /// <summary>Title markup as SubRip or WebVTT cue text.</summary>
    /// <param name="markup">The cue's text.</param>
    /// <param name="entities">True for WebVTT, which must escape <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c>.</param>
    public static string ToHtml(string markup, bool entities)
    {
        TitleText parsed = TitleMarkup.Parse(markup);
        var html = new StringBuilder(parsed.Plain.Length + 16);

        foreach ((string text, TitleStyle style) in Runs(parsed))
        {
            // WebVTT colours text only through classes: a <c> span whose class the file's style
            // block colours (VttFormat.Write); SubRip has a font tag.
            bool colour = style.Color is not null;
            if (style.Bold)
            {
                html.Append("<b>");
            }

            if (style.Italic)
            {
                html.Append("<i>");
            }

            if (style.Underline)
            {
                html.Append("<u>");
            }

            if (colour)
            {
                html.Append(entities ? $"<c.{VttClass(style.Color!)}>" : $"<font color=\"{style.Color}\">");
            }

            foreach (char c in text)
            {
                html.Append(entities ? c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", _ => c.ToString() } : c.ToString());
            }

            if (colour)
            {
                html.Append(entities ? "</c>" : "</font>");
            }

            if (style.Underline)
            {
                html.Append("</u>");
            }

            if (style.Italic)
            {
                html.Append("</i>");
            }

            if (style.Bold)
            {
                html.Append("</b>");
            }
        }

        return html.ToString();
    }

    /// <summary>The markup's text in runs of one style, gaps filled plain.</summary>
    internal static IEnumerable<(string Text, TitleStyle Style)> Runs(TitleText text)
    {
        int at = 0;
        foreach (TitleSpan span in text.Spans.OrderBy(span => span.Start))
        {
            if (span.Start > at)
            {
                yield return (text.Plain[at..span.Start], TitleStyle.Plain);
            }

            if (span.Length > 0)
            {
                yield return (text.Plain.Substring(span.Start, span.Length), span.Style);
            }

            at = Math.Max(at, span.End);
        }

        if (at < text.Plain.Length)
        {
            yield return (text.Plain[at..], TitleStyle.Plain);
        }
    }

    /// <summary>A colour as sRGB hex, from a hex value or a CSS colour name; null when it is neither.</summary>
    internal static string? Colour(string value)
    {
        string trimmed = value.Trim().Trim('"', '\'');
        if (Named.TryGetValue(trimmed, out string? named))
        {
            return named;
        }

        string hex = trimmed.TrimStart('#');
        return hex.Length is 6 or 8 && hex.All(Uri.IsHexDigit) ? "#" + hex[..6].ToUpperInvariant() : null;
    }

    /// <summary>The alignment an ASS override block's <c>\anN</c> sets, or null.</summary>
    internal static SubtitleAlign? AlignTag(string block)
    {
        foreach (string tag in block.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tag.Length == 3 && tag.StartsWith("an", StringComparison.OrdinalIgnoreCase) && tag[2] is >= '1' and <= '9')
            {
                return (SubtitleAlign)(tag[2] - '0');
            }
        }

        return null;
    }

    /// <summary>Appends text to markup with the markup's own characters escaped.</summary>
    internal static void Append(StringBuilder markup, string text)
    {
        foreach (char c in text)
        {
            Append(markup, c);
        }
    }

    private static void Append(StringBuilder markup, char c)
    {
        switch (c)
        {
            case '[':
                markup.Append("\\[");
                break;
            case '\\':
                markup.Append("\\\\");
                break;
            default:
                markup.Append(c);
                break;
        }
    }

    /// <summary>The markup for a tag this knows, or null.</summary>
    private static string? Tag(string tag, Stack<bool> fonts, IReadOnlyDictionary<string, string>? classes)
    {
        string name = tag.Split([' ', '.', '\t'], 2)[0].ToLowerInvariant();
        switch (name)
        {
            case "b":
            case "i":
            case "u":
                return "[" + name + "]";
            case "/b":
            case "/i":
            case "/u":
                return "[" + name + "]";
            case "font":
                string? colour = Attribute(tag, "color") is { } value ? Colour(value) : null;
                fonts.Push(colour is not null);
                return colour is null ? string.Empty : $"[color={colour}]";
            case "/font":
            case "/c":
                return fonts.Count > 0 && fonts.Pop() ? "[/color]" : string.Empty;
            case "c":
                // <c.yellow.bg_black>: the first class with a colour, the file's own or WebVTT's.
                string? classed = tag.Split('.').Skip(1)
                    .Select(name => classes is not null && classes.TryGetValue(name, out string? own) ? own : VttDefault.GetValueOrDefault(name))
                    .FirstOrDefault(found => found is not null);
                fonts.Push(classed is not null);
                return classed is null ? string.Empty : $"[color={classed}]";
            default:
                return null;
        }
    }

    /// <summary>True for anything that reads as a tag: a letter, a slash or a timestamp after the bracket.</summary>
    private static bool IsTagLike(string tag) =>
        tag.Length > 0 && (char.IsLetter(tag[0]) || tag[0] == '/' || char.IsDigit(tag[0]));

    private static string? Attribute(string tag, string name)
    {
        int at = tag.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        if (at < 0 || tag.IndexOf('=', at) is not (int equals and >= 0))
        {
            return null;
        }

        string rest = tag[(equals + 1)..].TrimStart();
        if (rest.Length > 0 && rest[0] is '"' or '\'')
        {
            int closing = rest.IndexOf(rest[0], 1);
            return closing > 0 ? rest[1..closing] : rest[1..];
        }

        int space = rest.IndexOf(' ');
        return space < 0 ? rest : rest[..space];
    }

    private static string? Entity(string name) => name switch
    {
        "amp" => "&",
        "lt" => "<",
        "gt" => ">",
        "quot" => "\"",
        "apos" => "'",
        "nbsp" => " ",
        "lrm" => "‎",
        "rlm" => "‏",
        _ when name.StartsWith("#x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex) => char.ConvertFromUtf32(hex),
        _ when name.StartsWith('#')
            && int.TryParse(name[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int code) => char.ConvertFromUtf32(code),
        _ => null,
    };
}
