using System.Globalization;
using System.Text;
using JazzHands.Core.Model;
using JazzHands.Core.Titles;

namespace JazzHands.Core.Subtitles;

/// <summary>
/// Advanced SubStation Alpha (and SSA): a script's info, its styles, then its events.
/// </summary>
/// <remarks>
/// <para>
/// A dialogue line's text carries override tags in braces. <c>\b</c>, <c>\i</c>, <c>\u</c>,
/// <c>\c</c> (or <c>\1c</c>), <c>\fn</c> and <c>\an</c> become markup and alignment; for any
/// other the line's text is kept as it was, so writing ASS back loses nothing, and of those
/// <c>\pos</c>, <c>\fad</c>, <c>\fs</c>, <c>\bord</c> and <c>\shad</c> are drawn from it
/// (<see cref="AssOverrides"/>); karaoke, drawings, <c>\move</c> and the rest are not. <c>\N</c> is a line
/// break, <c>\h</c> a space that does not break, and <c>\n</c> a space.
/// </para>
/// <para>
/// The style named <c>Default</c>, or the first, becomes the file's style, its sizes divided by
/// the script's height (<c>PlayResY</c>, 288 when unsaid). Everything before the events is kept
/// as the header, so a file read and written again keeps its styles.
/// </para>
/// </remarks>
public static class AssFormat
{
    private const string EventFormat = "Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text";

    /// <summary>Reads an ASS or SSA file's text.</summary>
    public static SubtitleDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = SubtitleFiles.Lines(text);
        var warnings = new List<string>();
        var cues = new List<SubtitleCue>();
        var styles = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string? firstStyle = null;
        string section = string.Empty;
        string[] styleFormat = [];
        string[] eventFormat = [.. EventFormat.Split(',').Select(name => name.Trim())];
        double playHeight = 288;
        var header = new StringBuilder();
        bool inEvents = false;

        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                inEvents = section.Equals("Events", StringComparison.OrdinalIgnoreCase);
            }

            if (!inEvents)
            {
                header.Append(line).Append('\n');
            }

            int colon = line.IndexOf(':');
            if (colon < 0 || line.StartsWith(';'))
            {
                continue;
            }

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].TrimStart();

            if (section.Equals("Script Info", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("PlayResY", StringComparison.OrdinalIgnoreCase) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double height) && height > 0)
                {
                    playHeight = height;
                }
            }
            else if (section.EndsWith("Styles", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("Format", StringComparison.OrdinalIgnoreCase))
                {
                    styleFormat = [.. value.Split(',').Select(name => name.Trim())];
                }
                else if (key.Equals("Style", StringComparison.OrdinalIgnoreCase) && styleFormat.Length > 0)
                {
                    Dictionary<string, string> fields = Fields(value, styleFormat);
                    string name = fields.GetValueOrDefault("Name", "Default");
                    styles[name] = fields;
                    firstStyle ??= name;
                }
            }
            else if (inEvents)
            {
                if (key.Equals("Format", StringComparison.OrdinalIgnoreCase))
                {
                    eventFormat = [.. value.Split(',').Select(name => name.Trim())];
                }
                else if (key.Equals("Dialogue", StringComparison.OrdinalIgnoreCase))
                {
                    Dictionary<string, string> fields = Fields(value, eventFormat);
                    if (SubtitleFiles.ReadClock(fields.GetValueOrDefault("Start", string.Empty)) is not { } start
                        || SubtitleFiles.ReadClock(fields.GetValueOrDefault("End", string.Empty)) is not { } end)
                    {
                        SrtFormat.Warn(warnings, "A dialogue line with unreadable times was skipped.");
                        continue;
                    }

                    string styleName = fields.GetValueOrDefault("Style", "Default").TrimStart('*');
                    string body = fields.GetValueOrDefault("Text", string.Empty);
                    (string markup, SubtitleAlign? tagged, List<string> dropped) = FromAss(body);
                    foreach (string tag in dropped.Where(kept => !AssOverrides.Drawn.Contains(kept)))
                    {
                        SrtFormat.Warn(warnings, $"The ASS tag \\{tag} is kept for writing ASS back but not drawn.");
                    }

                    SubtitleAlign align = tagged ?? StyleAlign(styles.GetValueOrDefault(styleName));
                    string? name = fields.GetValueOrDefault("Name") is { Length: > 0 } speaker ? speaker : null;
                    cues.Add(new SubtitleCue(
                        start,
                        end < start ? start : end,
                        markup,
                        align,
                        styleName,
                        name,
                        dropped.Count > 0 ? body : null));
                }
            }
        }

        string? main = styles.ContainsKey("Default") ? "Default" : firstStyle;
        SubtitleStyle? style = main is not null ? Style(styles[main], playHeight) : null;
        string? kept = styles.Count > 0 ? header.ToString().TrimEnd('\n') + "\n" : null;
        return new SubtitleDocument(SubtitleFiles.Ordered(cues), style, [.. warnings], kept);
    }

    /// <summary>Writes cues as ASS: the header the file came with, or one made from the style.</summary>
    public static string Write(SubtitleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = new StringBuilder();
        text.Append(document.Header ?? Header(document.Style ?? SubtitleStyle.Default));
        text.Append("\n[Events]\nFormat: ").Append(EventFormat).Append('\n');

        // A line needs an \an tag only where it sits somewhere its style does not put it.
        Dictionary<string, SubtitleAlign> aligns = document.Header is { } header ? StyleAligns(header) : new(StringComparer.OrdinalIgnoreCase);

        foreach (SubtitleCue cue in document.Cues)
        {
            string style = document.Header is not null ? cue.Style ?? "Default" : "Default";
            SubtitleAlign styleAlign = aligns.GetValueOrDefault(style, SubtitleAlign.Bottom);
            text.Append("Dialogue: 0,")
                .Append(SubtitleFiles.Clock(cue.Start, SubtitleFormat.Ass)).Append(',')
                .Append(SubtitleFiles.Clock(cue.End, SubtitleFormat.Ass)).Append(',')
                .Append(style).Append(',')
                .Append(cue.Name?.Replace(',', ' ') ?? string.Empty)
                .Append(",0,0,0,,")
                .Append(Body(cue, styleAlign))
                .Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// A line's text as markup, the alignment its tags set, and the names of the tags that could
    /// not be kept in markup.
    /// </summary>
    public static (string Markup, SubtitleAlign? Align, List<string> Dropped) FromAss(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var markup = new StringBuilder(text.Length);
        var dropped = new List<string>();
        SubtitleAlign? align = null;
        bool bold = false, italic = false, underline = false, colour = false, font = false;

        void Toggle(ref bool state, bool on, string tag)
        {
            if (state != on)
            {
                markup.Append(on ? $"[{tag}]" : $"[/{tag}]");
                state = on;
            }
        }

        for (int at = 0; at < text.Length; at++)
        {
            char c = text[at];
            if (c == '{')
            {
                int close = text.IndexOf('}', at);
                if (close < 0)
                {
                    SubtitleText.Append(markup, text[at..]);
                    break;
                }

                foreach (string tag in text[(at + 1)..close].Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = tag.Trim();
                    string name = new([.. trimmed.TakeWhile(char.IsLetter)]);
                    string argument = trimmed[name.Length..];

                    // The number in 1c belongs to the name, as in \1c&H0000FF&.
                    if (name.Length == 0 && trimmed.Length > 1 && char.IsDigit(trimmed[0]) && char.IsLetter(trimmed[1]))
                    {
                        name = trimmed[..2];
                        argument = trimmed[2..].TrimStart('c');
                    }

                    switch (name)
                    {
                        case "b":
                            Toggle(ref bold, argument.Length == 0 || (int.TryParse(argument, out int weight) && (weight == 1 || weight >= 600)), "b");
                            break;
                        case "i":
                            Toggle(ref italic, argument.Length == 0 || argument == "1", "i");
                            break;
                        case "u":
                            Toggle(ref underline, argument.Length == 0 || argument == "1", "u");
                            break;
                        case "c" or "1c":
                            if (colour)
                            {
                                markup.Append("[/color]");
                                colour = false;
                            }

                            if (Colour(argument) is { } hex)
                            {
                                markup.Append("[color=").Append(hex[..7]).Append(']');
                                colour = true;
                            }

                            break;
                        case "fn":
                            if (font)
                            {
                                markup.Append("[/font]");
                                font = false;
                            }

                            if (argument.Trim().Length > 0)
                            {
                                markup.Append("[font=").Append(argument.Trim().Replace("]", string.Empty, StringComparison.Ordinal)).Append(']');
                                font = true;
                            }

                            break;
                        case "an":
                            if (argument.Length == 1 && argument[0] is >= '1' and <= '9')
                            {
                                align = (SubtitleAlign)(argument[0] - '0');
                            }

                            break;
                        default:
                            if (name.Length > 0 && !dropped.Contains(name))
                            {
                                dropped.Add(name);
                            }

                            break;
                    }
                }

                at = close;
                continue;
            }

            if (c == '\\' && at + 1 < text.Length && text[at + 1] is 'N' or 'n' or 'h')
            {
                markup.Append(text[at + 1] switch { 'N' => "\n", 'h' => " ", _ => " " });
                at++;
                continue;
            }

            SubtitleText.Append(markup, c.ToString());
        }

        // Closed at the end, so the same text always reads to the same markup.
        Toggle(ref font, false, "font");
        Toggle(ref colour, false, "color");
        Toggle(ref underline, false, "u");
        Toggle(ref italic, false, "i");
        Toggle(ref bold, false, "b");
        return (markup.ToString(), align, dropped);
    }

    /// <summary>Markup as an ASS line's text.</summary>
    public static string ToAss(string markup)
    {
        TitleText parsed = TitleMarkup.Parse(markup);
        var text = new StringBuilder();
        TitleStyle current = TitleStyle.Plain;

        foreach ((string run, TitleStyle style) in SubtitleText.Runs(parsed))
        {
            var tags = new StringBuilder();
            if (style.Bold != current.Bold)
            {
                tags.Append(style.Bold ? "\\b1" : "\\b0");
            }

            if (style.Italic != current.Italic)
            {
                tags.Append(style.Italic ? "\\i1" : "\\i0");
            }

            if (style.Underline != current.Underline)
            {
                tags.Append(style.Underline ? "\\u1" : "\\u0");
            }

            if (style.Color != current.Color)
            {
                // An override colour is &HBBGGRR&, without the style colour's alpha.
                tags.Append(style.Color is { } hex ? "\\c&H" + AssColour(hex)[4..] + "&" : "\\c");
            }

            if (style.Font != current.Font)
            {
                tags.Append("\\fn").Append(style.Font ?? string.Empty);
            }

            if (tags.Length > 0)
            {
                text.Append('{').Append(tags).Append('}');
            }

            text.Append(run.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\\N", StringComparison.Ordinal).Replace('{', '(').Replace('}', ')'));
            current = style;
        }

        return text.ToString();
    }

    /// <summary>A cue's text as an ASS event's, with an alignment tag unless it sits at the bottom.</summary>
    public static string Line(SubtitleCue cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        return Body(cue, SubtitleAlign.Bottom);
    }

    /// <summary>
    /// The script header an ASS encoder is opened with: the style, and the events' format line,
    /// with no events.
    /// </summary>
    public static string EncoderHeader(SubtitleStyle? style) => Write(new SubtitleDocument([], style ?? SubtitleStyle.Default));

    /// <summary>
    /// A cue's text for writing: the line as the file had it while the markup and place still say
    /// the same, otherwise made from the markup.
    /// </summary>
    private static string Body(SubtitleCue cue, SubtitleAlign styleAlign)
    {
        if (cue.Raw is { } raw)
        {
            (string markup, SubtitleAlign? align, _) = FromAss(raw);
            if (markup == cue.Text && (align ?? styleAlign) == cue.Align)
            {
                return raw;
            }
        }

        string body = ToAss(cue.Text);
        return cue.Align == styleAlign ? body : $"{{\\an{(int)cue.Align}}}" + body;
    }

    /// <summary>A header with one Default style for a 1080 line script.</summary>
    private static string Header(SubtitleStyle style)
    {
        const double Height = 1080;
        var invariant = CultureInfo.InvariantCulture;
        bool boxed = Alpha(style.Box) > 0;
        string back = boxed ? AssColour(style.Box) : AssColour(style.Shadow);
        double outline = boxed ? 0 : style.OutlineWidth * Height;
        double shadow = boxed || Alpha(style.Shadow) == 0 ? 0 : 2;
        int bold = style.Weight is "bold" or "extra-bold" or "black" ? -1 : 0;
        int margin = (int)Math.Round(style.Margin * Height);

        return string.Create(invariant, $"""
            [Script Info]
            ; Written by Jazz Hands
            ScriptType: v4.00+
            PlayResX: 1920
            PlayResY: 1080
            WrapStyle: 0
            ScaledBorderAndShadow: yes

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,{style.Font},{Math.Round(style.Size * Height, 1)},{AssColour(style.Color)},&H000000FF,{AssColour(style.Outline)},{back},{bold},{(style.Italic ? -1 : 0)},0,0,100,100,0,0,{(boxed ? 3 : 1)},{Math.Round(outline, 1)},{shadow},2,{margin},{margin},{margin},1

            """).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>A style's fields as a subtitle style, sizes divided by the script's height.</summary>
    private static SubtitleStyle Style(Dictionary<string, string> fields, double height)
    {
        double Number(string name, double fallback) =>
            double.TryParse(fields.GetValueOrDefault(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;

        bool boxed = fields.GetValueOrDefault("BorderStyle") == "3";
        string back = Colour(fields.GetValueOrDefault("BackColour", "&H80000000")) ?? "#00000080";
        return SubtitleStyle.Default with
        {
            Font = fields.GetValueOrDefault("Fontname", SubtitleStyle.Default.Font),
            Weight = fields.GetValueOrDefault("Bold") is "-1" or "1" ? "bold" : "regular",
            Italic = fields.GetValueOrDefault("Italic") is "-1" or "1",
            Size = Number("Fontsize", 20) / height,
            Color = Colour(fields.GetValueOrDefault("PrimaryColour", "&H00FFFFFF")) ?? "#FFFFFF",
            Outline = Colour(fields.GetValueOrDefault("OutlineColour", "&H00000000")) ?? "#000000",
            OutlineWidth = boxed ? 0 : Number("Outline", 2) / height,
            Box = boxed ? back : "#00000000",
            Shadow = !boxed && Number("Shadow", 0) > 0 ? back : "#00000000",
            Margin = Number("MarginV", 10) / height,
        };
    }

    /// <summary>Where each style of a kept header puts its lines.</summary>
    private static Dictionary<string, SubtitleAlign> StyleAligns(string header)
    {
        var aligns = new Dictionary<string, SubtitleAlign>(StringComparer.OrdinalIgnoreCase);
        string[] format = [];
        foreach (string line in SubtitleFiles.Lines(header))
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].TrimStart();
            if (key.Equals("Format", StringComparison.OrdinalIgnoreCase))
            {
                format = [.. value.Split(',').Select(name => name.Trim())];
            }
            else if (key.Equals("Style", StringComparison.OrdinalIgnoreCase) && format.Length > 0)
            {
                Dictionary<string, string> fields = Fields(value, format);
                aligns[fields.GetValueOrDefault("Name", "Default")] = StyleAlign(fields);
            }
        }

        return aligns;
    }

    private static SubtitleAlign StyleAlign(Dictionary<string, string>? style) =>
        style?.GetValueOrDefault("Alignment") is { } value && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number is >= 1 and <= 9
            ? (SubtitleAlign)number
            : SubtitleAlign.Bottom;

    /// <summary>A line's comma separated values by the section's format; the last field takes the rest.</summary>
    private static Dictionary<string, string> Fields(string value, string[] format)
    {
        string[] parts = value.Split(',', format.Length);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < Math.Min(parts.Length, format.Length); index++)
        {
            fields[format[index]] = index == format.Length - 1 ? parts[index] : parts[index].Trim();
        }

        return fields;
    }

    /// <summary>
    /// An ASS colour, <c>&amp;HAABBGGRR&amp;</c> with alpha inverted (00 opaque), as sRGB hex,
    /// with the alpha written only when it is not opaque.
    /// </summary>
    internal static string? Colour(string value)
    {
        string hex = value.Trim().Trim('&').TrimStart('H', 'h').TrimEnd('&');
        if (hex.Length == 0 || hex.Length > 8 || !hex.All(Uri.IsHexDigit))
        {
            return null;
        }

        uint bits = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        uint alpha = 255 - (bits >> 24);
        string rgb = string.Create(CultureInfo.InvariantCulture, $"#{bits & 0xFF:X2}{(bits >> 8) & 0xFF:X2}{(bits >> 16) & 0xFF:X2}");
        return alpha == 255 ? rgb : rgb + alpha.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>sRGB hex, with or without alpha, as an ASS colour.</summary>
    internal static string AssColour(string hex)
    {
        string digits = hex.TrimStart('#');
        uint r = Convert.ToUInt32(digits[0..2], 16);
        uint g = Convert.ToUInt32(digits[2..4], 16);
        uint b = Convert.ToUInt32(digits[4..6], 16);
        uint a = digits.Length >= 8 ? Convert.ToUInt32(digits[6..8], 16) : 255;
        return string.Create(CultureInfo.InvariantCulture, $"&H{255 - a:X2}{b:X2}{g:X2}{r:X2}");
    }

    private static uint Alpha(string hex)
    {
        string digits = hex.TrimStart('#');
        return digits.Length >= 8 ? Convert.ToUInt32(digits[6..8], 16) : 255;
    }
}
