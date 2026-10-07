using System.Globalization;
using System.Numerics;
using System.Text;

namespace JazzHands.Core.Subtitles;

/// <summary>
/// The ASS override tags a cue's kept line carries that are drawn beyond its markup: where it
/// sits (<c>\pos</c>), how it fades (<c>\fad</c>), its size (<c>\fs</c>), its outline
/// (<c>\bord</c>) and its shadow (<c>\shad</c>), in the 1920 by 1080 script space a track keeps.
/// </summary>
/// <remarks>
/// <para>
/// ASS lets a tag change part way through a line; a title is one style, so the first of each in
/// the line stands for the whole line. Karaoke, drawings, <c>\move</c>, rotations and the rest
/// are kept for writing ASS back and not drawn.
/// </para>
/// <para>
/// A script's coordinates are in its own play resolution. A track writes ASS as a 1920 by 1080
/// script, so the lines of a file are rescaled to that as they come onto a track
/// (<see cref="Rescale"/>), and from then on mean the same thing drawn and written.
/// </para>
/// </remarks>
/// <param name="Position">Where the cue's anchor (its alignment's point) sits, in script pixels from the top left.</param>
/// <param name="FadeIn">How long it fades in, in milliseconds.</param>
/// <param name="FadeOut">How long it fades out, in milliseconds.</param>
/// <param name="Size">The text's height, in script pixels.</param>
/// <param name="Border">The outline's width, in script pixels.</param>
/// <param name="Shadow">The shadow's offset, in script pixels; 0 for none.</param>
public sealed record AssOverrides(
    Vector2? Position = null,
    int FadeIn = 0,
    int FadeOut = 0,
    double? Size = null,
    double? Border = null,
    double? Shadow = null)
{
    /// <summary>The script space a track keeps cues in, as its ASS export declares.</summary>
    public static readonly Vector2 ScriptSize = new(1920, 1080);

    /// <summary>The tags this draws; others are kept and not drawn.</summary>
    public static IReadOnlySet<string> Drawn { get; } = new HashSet<string>(StringComparer.Ordinal) { "pos", "fad", "fs", "bord", "shad" };

    /// <summary>True when there is anything to draw.</summary>
    public bool Any => Position is not null || FadeIn > 0 || FadeOut > 0 || Size is not null || Border is not null || Shadow is not null;

    /// <summary>The drawn tags of an ASS line, the first of each; null when it has none.</summary>
    public static AssOverrides? Read(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var found = new AssOverrides();
        bool position = false, fade = false, size = false, border = false, shadow = false;
        foreach ((string name, string argument) in Tags(line))
        {
            double[] numbers = Numbers(argument);
            switch (name)
            {
                case "pos" when !position && numbers.Length == 2:
                    found = found with { Position = new Vector2((float)numbers[0], (float)numbers[1]) };
                    position = true;
                    break;
                case "fad" when !fade && numbers.Length == 2:
                    found = found with { FadeIn = Math.Max(0, (int)numbers[0]), FadeOut = Math.Max(0, (int)numbers[1]) };
                    fade = true;
                    break;
                case "fs" when !size && numbers.Length == 1 && numbers[0] > 0:
                    found = found with { Size = numbers[0] };
                    size = true;
                    break;
                case "bord" when !border && numbers.Length == 1 && numbers[0] >= 0:
                    found = found with { Border = numbers[0] };
                    border = true;
                    break;
                case "shad" when !shadow && numbers.Length == 1 && numbers[0] >= 0:
                    found = found with { Shadow = numbers[0] };
                    shadow = true;
                    break;
            }
        }

        return found.Any ? found : null;
    }

    /// <summary>
    /// An ASS line with its places and sizes moved from a script of one play resolution to the
    /// 1920 by 1080 one a track keeps: across by the widths, down and sizes by the heights.
    /// </summary>
    /// <param name="line">The line's text, override blocks and all.</param>
    /// <param name="playResolution">The script's PlayResX and PlayResY.</param>
    public static string Rescale(string line, Vector2 playResolution)
    {
        ArgumentNullException.ThrowIfNull(line);
        double across = ScriptSize.X / playResolution.X;
        double down = ScriptSize.Y / playResolution.Y;
        if ((Math.Abs(across - 1) < 1e-9 && Math.Abs(down - 1) < 1e-9) || playResolution.X <= 0 || playResolution.Y <= 0)
        {
            return line;
        }

        var result = new StringBuilder(line.Length + 16);
        for (int at = 0; at < line.Length; at++)
        {
            int close = line[at] == '{' ? line.IndexOf('}', at) : -1;
            if (close < 0)
            {
                result.Append(line[at]);
                continue;
            }

            string[] tags = line[(at + 1)..close].Split('\\');
            result.Append('{').Append(tags[0]);
            foreach (string tag in tags.Skip(1))
            {
                result.Append('\\').Append(RescaleTag(tag, across, down));
            }

            result.Append('}');
            at = close;
        }

        return result.ToString();
    }

    /// <summary>A script's play resolution from its header, as libass reads it: 384 by 288 when neither is said.</summary>
    public static Vector2 PlayResolution(string? header)
    {
        double width = 0, height = 0;
        foreach (string raw in (header ?? string.Empty).Split('\n'))
        {
            string line = raw.Trim();
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            string key = line[..colon].Trim();
            if (double.TryParse(line[(colon + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0)
            {
                if (key.Equals("PlayResX", StringComparison.OrdinalIgnoreCase))
                {
                    width = value;
                }
                else if (key.Equals("PlayResY", StringComparison.OrdinalIgnoreCase))
                {
                    height = value;
                }
            }
        }

        if (width <= 0 && height <= 0)
        {
            (width, height) = (384, 288);
        }
        else if (width <= 0)
        {
            width = height == 1024 ? 1280 : height * 4 / 3;
        }
        else if (height <= 0)
        {
            height = width == 1280 ? 1024 : width * 3 / 4;
        }

        return new Vector2((float)width, (float)height);
    }

    /// <summary>One tag with its places and sizes scaled; any other tag as it was.</summary>
    private static string RescaleTag(string tag, double across, double down)
    {
        string name = new([.. tag.TakeWhile(char.IsLetter)]);
        string argument = tag[name.Length..];
        double[] numbers = Numbers(argument);
        string Scaled(params double[] scales) => name + "(" + string.Join(',', numbers.Select((number, index) => Format(index < scales.Length ? number * scales[index] : number))) + ")";

        return name switch
        {
            "pos" or "org" when numbers.Length == 2 => Scaled(across, down),
            "move" when numbers.Length is 4 or 6 => Scaled(across, down, across, down),
            "clip" or "iclip" when numbers.Length == 4 => Scaled(across, down, across, down),
            "fs" or "bord" or "shad" or "be" or "blur" or "ybord" or "yshad" when numbers.Length == 1 => name + Format(numbers[0] * down),
            "xbord" or "xshad" or "fsp" when numbers.Length == 1 => name + Format(numbers[0] * across),
            _ => tag,
        };
    }

    private static string Format(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);

    /// <summary>The override tags of a line, by name and argument, in order.</summary>
    private static IEnumerable<(string Name, string Argument)> Tags(string line)
    {
        for (int open = line.IndexOf('{'); open >= 0; open = line.IndexOf('{', open + 1))
        {
            int close = line.IndexOf('}', open);
            if (close < 0)
            {
                yield break;
            }

            foreach (string tag in line[(open + 1)..close].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = tag.Trim();
                string name = new([.. trimmed.TakeWhile(char.IsLetter)]);
                yield return (name, trimmed[name.Length..]);
            }

            open = close;
        }
    }

    /// <summary>The numbers in a tag's argument: <c>(12,34)</c> or <c>36</c>.</summary>
    private static double[] Numbers(string argument) =>
        [.. argument.Trim().TrimStart('(').TrimEnd(')').Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN)
            .TakeWhile(value => !double.IsNaN(value))];
}
