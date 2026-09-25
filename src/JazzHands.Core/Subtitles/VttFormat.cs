using System.Globalization;
using System.Text;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Subtitles;

/// <summary>
/// WebVTT: a <c>WEBVTT</c> header, then blocks: cues with an optional id and settings, notes,
/// styles and regions.
/// </summary>
/// <remarks>
/// A cue's settings say where it sits: <c>line</c> (a line number from the top, negative from
/// the bottom, or a percentage) and <c>align</c> (start, centre, end). They are read into the
/// cue's alignment and kept as written for writing WebVTT back. A <c>::cue</c> style block's
/// colour, background and font become the file's style; other style rules, regions and notes are
/// skipped with a warning.
/// </remarks>
public static class VttFormat
{
    /// <summary>Reads a WebVTT file's text.</summary>
    public static SubtitleDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = SubtitleFiles.Lines(text);
        var cues = new List<SubtitleCue>();
        var warnings = new List<string>();
        SubtitleStyle? style = null;

        int at = 0;
        while (at < lines.Length && lines[at].Trim().Length == 0)
        {
            at++;
        }

        if (at < lines.Length && lines[at].StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            // The header runs to the first blank line.
            while (at < lines.Length && lines[at].Trim().Length > 0)
            {
                at++;
            }
        }
        else
        {
            SrtFormat.Warn(warnings, "The file does not start with WEBVTT; it was read anyway.");
        }

        while (at < lines.Length)
        {
            if (lines[at].Trim().Length == 0)
            {
                at++;
                continue;
            }

            int end = at;
            while (end < lines.Length && lines[end].Trim().Length > 0)
            {
                end++;
            }

            string[] block = lines[at..end];
            at = end;

            string first = block[0].Trim();
            if (first.StartsWith("NOTE", StringComparison.Ordinal))
            {
                continue;
            }

            if (first.StartsWith("STYLE", StringComparison.Ordinal))
            {
                style = Style(string.Join('\n', block[1..]), style ?? SubtitleStyle.Default, warnings);
                continue;
            }

            if (first.StartsWith("REGION", StringComparison.Ordinal))
            {
                SrtFormat.Warn(warnings, "WebVTT regions are not kept.");
                continue;
            }

            int timing = block[0].Contains("-->", StringComparison.Ordinal) ? 0 : 1;
            if (timing >= block.Length || Timing(block[timing]) is not { } time)
            {
                SrtFormat.Warn(warnings, "A block that is not a cue was skipped.");
                continue;
            }

            string? id = timing == 1 ? block[0].Trim() : null;
            Flicks stop = time.End < time.Start ? time.Start : time.End;
            (string markup, _) = SubtitleText.FromHtml(string.Join('\n', block[(timing + 1)..].Select(line => line.TrimEnd())), entities: true);
            cues.Add(new SubtitleCue(time.Start, stop, markup, Align(time.Settings), Name: id, Raw: time.Settings.Length > 0 ? time.Settings : null));
        }

        return new SubtitleDocument(SubtitleFiles.Ordered(cues), style, [.. warnings]);
    }

    /// <summary>Writes cues as WebVTT.</summary>
    public static string Write(SubtitleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = new StringBuilder("WEBVTT\n\n");

        foreach (SubtitleCue cue in document.Cues)
        {
            if (cue.Name is { Length: > 0 } name && !name.Contains("-->", StringComparison.Ordinal))
            {
                text.Append(name).Append('\n');
            }

            text.Append(SubtitleFiles.Clock(cue.Start, SubtitleFormat.Vtt)).Append(" --> ").Append(SubtitleFiles.Clock(cue.End, SubtitleFormat.Vtt));

            // The cue's own settings as the file had them, unless its place has changed since.
            string settings = cue.Raw is { } raw && Align(raw) == cue.Align ? raw : Settings(cue.Align);
            if (settings.Length > 0)
            {
                text.Append(' ').Append(settings);
            }

            // A blank line would end the cue, so lines are never empty.
            string body = SubtitleText.ToHtml(cue.Text, entities: true).Replace("\r\n", "\n", StringComparison.Ordinal);
            text.Append('\n').Append(string.Join('\n', body.Split('\n').Select(line => line.Length == 0 ? " " : line))).Append("\n\n");
        }

        return text.ToString();
    }

    /// <summary>The settings for an alignment: none for the bottom centre.</summary>
    internal static string Settings(SubtitleAlign align)
    {
        int number = (int)align;
        string line = number switch
        {
            >= 7 => "line:0",
            >= 4 => "line:50%",
            _ => string.Empty,
        };
        string side = (number % 3) switch
        {
            1 => "align:start",
            0 => "align:end",
            _ => string.Empty,
        };

        return string.Join(' ', new[] { line, side }.Where(part => part.Length > 0));
    }

    /// <summary>Where a cue's settings put it.</summary>
    internal static SubtitleAlign Align(string settings)
    {
        int row = 0;
        int column = 1;
        foreach (string setting in settings.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = setting.Split(':', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            string value = pair[1].Split(',')[0];
            switch (pair[0])
            {
                case "line":
                    if (value.EndsWith('%') && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
                    {
                        row = percent < 34 ? 2 : percent < 67 ? 1 : 0;
                    }
                    else if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number))
                    {
                        row = number >= 0 ? 2 : 0;
                    }

                    break;
                case "align":
                    column = value switch
                    {
                        "start" or "left" => 0,
                        "end" or "right" => 2,
                        _ => 1,
                    };
                    break;
            }
        }

        return (SubtitleAlign)((row * 3) + column + 1);
    }

    private static (Flicks Start, Flicks End, string Settings)? Timing(string line)
    {
        int arrow = line.IndexOf("-->", StringComparison.Ordinal);
        if (arrow < 0)
        {
            return null;
        }

        string right = line[(arrow + 3)..].Trim();
        int space = right.IndexOfAny([' ', '\t']);
        string end = space < 0 ? right : right[..space];
        string settings = space < 0 ? string.Empty : right[(space + 1)..].Trim();

        return SubtitleFiles.ReadClock(line[..arrow]) is { } start && SubtitleFiles.ReadClock(end) is { } stop
            ? (start, stop, settings)
            : null;
    }

    /// <summary>A <c>::cue</c> rule's colour, background and font as a style.</summary>
    private static SubtitleStyle Style(string css, SubtitleStyle style, List<string> warnings)
    {
        int open = css.IndexOf("::cue", StringComparison.Ordinal);
        int brace = open < 0 ? -1 : css.IndexOf('{', open);
        int close = brace < 0 ? -1 : css.IndexOf('}', brace);
        if (brace < 0 || close < 0 || css[(open + 5)..brace].Trim().Length > 0)
        {
            SrtFormat.Warn(warnings, "WebVTT style rules other than a plain ::cue are not kept.");
            return style;
        }

        foreach (string declaration in css[(brace + 1)..close].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = declaration.Split(':', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            string value = pair[1].Trim();
            style = pair[0].Trim().ToLowerInvariant() switch
            {
                "color" when SubtitleText.Colour(value) is { } colour => style with { Color = colour },
                "background-color" or "background" when SubtitleText.Colour(value) is { } box => style with { Box = box + "FF" },
                "font-family" => style with { Font = value.Split(',')[0].Trim().Trim('"', '\'') },
                _ => style,
            };
        }

        return style;
    }
}
