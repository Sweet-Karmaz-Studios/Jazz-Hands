using System.Collections.Immutable;
using System.Text;
using JazzHands.Core.Model;
using JazzHands.Core.Titles;

namespace JazzHands.Core.Subtitles;

/// <summary>
/// Edits to a cue's text that keep its formatting on the words it covered: breaking it into
/// lines and cues, and finding and replacing.
/// </summary>
/// <remarks>
/// Both work on the text as it reads, one character at a time with the style each character
/// has, and write the result back as markup; a word half in italics is found, and stays half in
/// italics after the line it is on is broken.
/// </remarks>
public static class CueText
{
    /// <summary>
    /// A cue's text laid out in lines of at most <paramref name="maxChars"/> characters, breaking
    /// between words, and cut into as many cues as keep each to <paramref name="maxLines"/> lines.
    /// A cut falls after a sentence where one ends half way through a cue or later.
    /// </summary>
    /// <returns>The markup for each cue, and each one's share of the length, by characters.</returns>
    public static ImmutableArray<(string Markup, double Share)> Split(string markup, int maxChars, int maxLines)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);

        List<(char Character, TitleStyle Style)> text = Characters(TitleMarkup.Parse(markup));

        // Line breaks the cue already had become spaces, to be broken again where they fit.
        for (int index = 0; index < text.Count; index++)
        {
            if (text[index].Character is '\n' or '\r')
            {
                text[index] = (' ', text[index].Style);
            }
        }

        List<(int Start, int End)> words = Words(text);
        if (words.Count == 0)
        {
            return [(markup, 1.0)];
        }

        // Words into cues: a cue closes when the next word would need one line too many, or
        // after a sentence once the cue is half full.
        var cues = new List<List<(int Start, int End)>>();
        var current = new List<(int Start, int End)>();
        int capacity = maxChars * maxLines;

        foreach ((int Start, int End) word in words)
        {
            var trial = new List<(int Start, int End)>(current) { word };
            if (current.Count > 0 && Lines(trial, maxChars).Count > maxLines)
            {
                cues.Add(current);
                current = [word];
            }
            else
            {
                current = trial;
            }

            int length = current[^1].End - current[0].Start;
            if (text[word.End - 1].Character is '.' or '!' or '?' && length >= capacity / 2 && word != words[^1])
            {
                cues.Add(current);
                current = [];
            }
        }

        if (current.Count > 0)
        {
            cues.Add(current);
        }

        int total = cues.Sum(cue => cue.Sum(word => word.End - word.Start));
        return [.. cues.Select(cue =>
        {
            var built = new List<(char, TitleStyle)>();
            List<List<(int Start, int End)>> lines = Lines(cue, maxChars);
            for (int line = 0; line < lines.Count; line++)
            {
                if (line > 0)
                {
                    built.Add(('\n', TitleStyle.Plain));
                }

                for (int word = 0; word < lines[line].Count; word++)
                {
                    if (word > 0)
                    {
                        // The space between two words keeps the style it had.
                        built.Add((' ', text[lines[line][word].Start - 1].Style));
                    }

                    (int start, int end) = lines[line][word];
                    built.AddRange(text.GetRange(start, end - start));
                }
            }

            double share = (double)cue.Sum(word => word.End - word.Start) / Math.Max(1, total);
            return (Markup(built), share);
        })];
    }

    /// <summary>
    /// Replaces every occurrence of <paramref name="find"/> in the text as it reads. What goes in
    /// takes the style of the first character it replaced.
    /// </summary>
    /// <returns>The new markup, and how many were replaced.</returns>
    public static (string Markup, int Count) Replace(string markup, string find, string with, bool matchCase)
    {
        ArgumentException.ThrowIfNullOrEmpty(find);
        ArgumentNullException.ThrowIfNull(with);

        TitleText parsed = TitleMarkup.Parse(markup);
        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        List<(char Character, TitleStyle Style)> text = Characters(parsed);
        var result = new List<(char, TitleStyle)>(text.Count);
        int count = 0;
        int at = 0;

        while (at < text.Count)
        {
            int found = parsed.Plain.IndexOf(find, at, comparison);
            if (found < 0)
            {
                result.AddRange(text.GetRange(at, text.Count - at));
                break;
            }

            result.AddRange(text.GetRange(at, found - at));
            TitleStyle style = text[found].Style;
            result.AddRange(with.Select(character => (character, style)));
            at = found + find.Length;
            count++;
        }

        return count == 0 ? (markup, 0) : (Markup(result), count);
    }

    /// <summary>The text as it reads, a character at a time with each one's style.</summary>
    private static List<(char Character, TitleStyle Style)> Characters(TitleText text) =>
        [.. text.Plain.Select((character, index) => (character, text.StyleAt(index)))];

    /// <summary>Characters and their styles written back as markup.</summary>
    private static string Markup(List<(char Character, TitleStyle Style)> text)
    {
        var plain = new StringBuilder(text.Count);
        var spans = ImmutableArray.CreateBuilder<TitleSpan>();
        int start = 0;

        for (int index = 0; index <= text.Count; index++)
        {
            bool boundary = index == text.Count || (index > 0 && text[index].Style != text[index - 1].Style);
            if (boundary && index > start)
            {
                spans.Add(new TitleSpan(start, index - start, text[start].Style));
                start = index;
            }

            if (index < text.Count)
            {
                plain.Append(text[index].Character);
            }
        }

        return TitleMarkup.Format(new TitleText(plain.ToString(), new EquatableArray<TitleSpan>(spans.ToImmutable())));
    }

    /// <summary>The words of the text, as runs of characters that are not spaces.</summary>
    private static List<(int Start, int End)> Words(List<(char Character, TitleStyle Style)> text)
    {
        var words = new List<(int, int)>();
        int at = 0;
        while (at < text.Count)
        {
            while (at < text.Count && char.IsWhiteSpace(text[at].Character))
            {
                at++;
            }

            int start = at;
            while (at < text.Count && !char.IsWhiteSpace(text[at].Character))
            {
                at++;
            }

            if (at > start)
            {
                words.Add((start, at));
            }
        }

        return words;
    }

    /// <summary>Words into lines greedily, a word longer than a line on a line of its own.</summary>
    private static List<List<(int Start, int End)>> Lines(List<(int Start, int End)> words, int maxChars)
    {
        var lines = new List<List<(int, int)>>();
        var line = new List<(int Start, int End)>();
        int length = 0;

        foreach ((int Start, int End) word in words)
        {
            int size = word.End - word.Start;
            if (line.Count > 0 && length + 1 + size > maxChars)
            {
                lines.Add(line);
                line = [];
                length = 0;
            }

            length += (line.Count > 0 ? 1 : 0) + size;
            line.Add(word);
        }

        if (line.Count > 0)
        {
            lines.Add(line);
        }

        return lines;
    }
}
