using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Subtitles;

/// <summary>
/// SubRip: a number, a time line, the text, a blank line.
/// </summary>
/// <remarks>
/// Read leniently, because SubRip files are written by everything: a byte order mark, any line
/// breaks, missing or wrong numbers, a missing blank line between cues, a full stop for the
/// comma, one to three digits of milliseconds, overlapping and out of order cues, position
/// coordinates after the times. A cue that ends before it starts is given no length and a warning.
/// Written strictly: numbered from 1, CRLF, three digits of milliseconds.
/// </remarks>
public static class SrtFormat
{
    /// <summary>Reads a SubRip file's text.</summary>
    public static SubtitleDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = SubtitleFiles.Lines(text);
        var cues = new List<SubtitleCue>();
        var warnings = new List<string>();
        int at = 0;

        while (at < lines.Length)
        {
            if (lines[at].Trim().Length == 0)
            {
                at++;
                continue;
            }

            int timing;
            if (Timing(lines[at]) is not null)
            {
                timing = at;
            }
            else if (at + 1 < lines.Length && Timing(lines[at + 1]) is not null)
            {
                timing = at + 1;
            }
            else
            {
                Warn(warnings, "Text outside any cue was skipped.");
                at++;
                continue;
            }

            (Flicks start, Flicks end) = Timing(lines[timing])!.Value;
            int body = timing + 1;
            int stop = body;
            while (stop < lines.Length && lines[stop].Trim().Length > 0 && !StartsCue(lines, stop))
            {
                stop++;
            }

            if (end < start)
            {
                Warn(warnings, $"A cue at {SubtitleFiles.Clock(start, SubtitleFormat.Srt)} ended before it started; it was given no length.");
                end = start;
            }

            (string markup, SubtitleAlign? align) = SubtitleText.FromHtml(string.Join('\n', lines[body..stop].Select(line => line.TrimEnd())), entities: false);
            cues.Add(new SubtitleCue(start, end, markup, align ?? SubtitleAlign.Bottom));
            at = stop;
        }

        return new SubtitleDocument(SubtitleFiles.Ordered(cues), Warnings: [.. warnings]);
    }

    /// <summary>Writes cues as SubRip.</summary>
    public static string Write(SubtitleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = new StringBuilder();
        int number = 1;

        foreach (SubtitleCue cue in document.Cues)
        {
            text.Append(number.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            text.Append(SubtitleFiles.Clock(cue.Start, SubtitleFormat.Srt)).Append(" --> ").Append(SubtitleFiles.Clock(cue.End, SubtitleFormat.Srt)).Append("\r\n");

            string body = SubtitleText.ToHtml(cue.Text, entities: false);
            if (cue.Align != SubtitleAlign.Bottom)
            {
                body = $"{{\\an{(int)cue.Align}}}" + body;
            }

            text.Append(body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)).Append("\r\n\r\n");
            number++;
        }

        return text.ToString();
    }

    /// <summary>A time line's start and end, or null when the line is not one.</summary>
    internal static (Flicks Start, Flicks End)? Timing(string line)
    {
        int arrow = line.IndexOf("-->", StringComparison.Ordinal);
        if (arrow < 0)
        {
            return null;
        }

        string right = line[(arrow + 3)..].Trim();
        int space = right.IndexOfAny([' ', '\t']);
        return SubtitleFiles.ReadClock(line[..arrow]) is { } start && SubtitleFiles.ReadClock(space < 0 ? right : right[..space]) is { } end
            ? (start, end)
            : null;
    }

    /// <summary>True where a new cue begins without the blank line before it: a time line, or a number and then one.</summary>
    private static bool StartsCue(string[] lines, int at) =>
        Timing(lines[at]) is not null
        || (at + 1 < lines.Length && lines[at].Trim().All(char.IsDigit) && Timing(lines[at + 1]) is not null);

    internal static void Warn(List<string> warnings, string warning)
    {
        if (!warnings.Contains(warning))
        {
            warnings.Add(warning);
        }
    }
}
