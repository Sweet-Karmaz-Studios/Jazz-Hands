using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Core.Titles;

namespace JazzHands.Core.Subtitles;

/// <summary>
/// What makes a caption readable (Phase 39): the rules captions are laid out by and checked
/// against. The defaults are the common broadcast ones: 42 characters a line, two lines, 20
/// characters a second, on screen for five sixths of a second to seven seconds, and two frames
/// between cues that do not run straight on.
/// </summary>
/// <param name="MaxChars">The most characters a line.</param>
/// <param name="MaxLines">The most lines a cue.</param>
/// <param name="MaxCharsPerSecond">The fastest a cue can ask to be read, in characters a second, spaces counted.</param>
/// <param name="MinDuration">The shortest a cue can stay up.</param>
/// <param name="MaxDuration">The longest a cue can stay up.</param>
/// <param name="MinGapFrames">The fewest frames between one cue and the next, unless the next starts the frame the first ends.</param>
public sealed record CueRules(
    int MaxChars = 42,
    int MaxLines = 2,
    double MaxCharsPerSecond = 20,
    Flicks? MinDuration = null,
    Flicks? MaxDuration = null,
    int MinGapFrames = 2)
{
    /// <summary>Five sixths of a second: the shortest a caption can be read in.</summary>
    public static Flicks DefaultMinDuration { get; } = Flicks.OneSecond * 5 / 6;

    /// <summary>Seven seconds: longer and people read it twice.</summary>
    public static Flicks DefaultMaxDuration { get; } = Flicks.OneSecond * 7;

    /// <summary>The shortest a cue can stay up.</summary>
    public Flicks Shortest => MinDuration ?? DefaultMinDuration;

    /// <summary>The longest a cue can stay up.</summary>
    public Flicks Longest => MaxDuration ?? DefaultMaxDuration;

    /// <summary>The rules a subtitle track's style sets: its line length and line count.</summary>
    public static CueRules For(SubtitleStyle? style) => new(
        (style ?? SubtitleStyle.Default).MaxChars,
        (style ?? SubtitleStyle.Default).MaxLines);
}

/// <summary>Something about a cue that makes it hard to read.</summary>
/// <param name="CueId">The cue's clip id.</param>
/// <param name="Time">When it starts.</param>
/// <param name="Code"><c>line-too-long</c>, <c>too-many-lines</c>, <c>reads-too-fast</c>, <c>too-short</c>, <c>too-long</c> or <c>gap-too-small</c>.</param>
/// <param name="Message">What is wrong, in a sentence.</param>
public sealed record CueProblem(string CueId, Flicks Time, string Code, string Message);

/// <summary>The subtitle validator: checks cues against <see cref="CueRules"/>.</summary>
public static class CueChecks
{
    /// <summary>The problems with a subtitle track's cues, in time order.</summary>
    public static IReadOnlyList<CueProblem> Check(Track track, Rational frameRate, CueRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        return Check(
            [.. track.Clips.Where(clip => clip.Cue is not null && clip.Enabled).Select(clip => (clip.Id, clip.Start, clip.End, clip.Cue!.Text))],
            frameRate,
            rules ?? CueRules.For(track.SubtitleStyle));
    }

    /// <summary>The problems with some cues, in time order.</summary>
    public static IReadOnlyList<CueProblem> Check(IReadOnlyList<(string Id, Flicks Start, Flicks End, string Markup)> cues, Rational frameRate, CueRules rules)
    {
        ArgumentNullException.ThrowIfNull(cues);
        ArgumentNullException.ThrowIfNull(rules);
        var problems = new List<CueProblem>();
        var ordered = cues.OrderBy(cue => cue.Start).ToList();
        Flicks minGap = Flicks.FromFrames(rules.MinGapFrames, frameRate);

        for (int index = 0; index < ordered.Count; index++)
        {
            (string id, Flicks start, Flicks end, string markup) = ordered[index];
            string[] lines = TitleMarkup.PlainText(markup).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
            Flicks length = end - start;

            if (lines.FirstOrDefault(line => line.Length > rules.MaxChars) is { } wide)
            {
                problems.Add(new(id, start, "line-too-long", $"A line has {wide.Length} characters; the most is {rules.MaxChars}."));
            }

            if (lines.Length > rules.MaxLines)
            {
                problems.Add(new(id, start, "too-many-lines", $"It has {lines.Length} lines; the most is {rules.MaxLines}."));
            }

            int characters = lines.Sum(line => line.Length) + Math.Max(0, lines.Length - 1);
            double seconds = length.ToSeconds();
            if (seconds > 0 && characters / seconds > rules.MaxCharsPerSecond + 1e-9)
            {
                problems.Add(new(id, start, "reads-too-fast", FormattableString.Invariant($"It asks for {characters / seconds:F1} characters a second; the most is {rules.MaxCharsPerSecond:0.#}.")));
            }

            if (length < rules.Shortest)
            {
                problems.Add(new(id, start, "too-short", FormattableString.Invariant($"It is up for {seconds:F2} s; the shortest is {rules.Shortest.ToSeconds():F2} s.")));
            }

            if (length > rules.Longest)
            {
                problems.Add(new(id, start, "too-long", FormattableString.Invariant($"It is up for {seconds:F2} s; the longest is {rules.Longest.ToSeconds():0.#} s.")));
            }

            if (index + 1 < ordered.Count)
            {
                Flicks gap = ordered[index + 1].Start - end;
                if (gap > Flicks.Zero && gap < minGap)
                {
                    problems.Add(new(id, start, "gap-too-small", $"The next cue follows it by less than {rules.MinGapFrames} frames; run straight on or leave {rules.MinGapFrames}."));
                }
            }
        }

        return problems;
    }
}

/// <summary>
/// Captions from words (Phase 39): cues laid out by <see cref="CueRules"/>, for
/// <c>subtitle.from-transcript</c>.
/// </summary>
public static class CaptionLayout
{
    /// <summary>A pause this long between words starts a new cue.</summary>
    public static Flicks PauseBreak { get; } = Flicks.FromMilliseconds(600);

    /// <summary>A gap to the next cue shorter than this is closed up to the minimum gap.</summary>
    private static readonly Flicks CloseUp = Flicks.FromMilliseconds(500);

    /// <summary>
    /// Cues for words said at timeline times. The words are first cut into phrases at sentence
    /// ends and pauses; a phrase too long for one cue is shared among as few cues as hold it, as
    /// evenly as they go, so a long sentence never ends on a stub. Lines are balanced (two lines
    /// of a cue about the same length). A cue starts on the frame its first word starts and stays
    /// up to its last word's end, longer when it would be too fast to read (as far as the next
    /// allows) or too short (pushing the next a little later), never closer than the minimum gap
    /// to the next; a gap shorter than half a second is closed to that minimum, so captions do not
    /// flicker off between phrases.
    /// </summary>
    public static IReadOnlyList<(Flicks Start, Flicks End, string Text)> Cues(
        IReadOnlyList<(string Text, Flicks Start, Flicks End)> words,
        CueRules rules,
        Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(rules);
        var groups = new List<List<(string Text, Flicks Start, Flicks End)>>();
        foreach (List<(string Text, Flicks Start, Flicks End)> phrase in Phrases(words))
        {
            groups.AddRange(Share(phrase, rules));
        }

        Flicks minGap = Flicks.FromFrames(rules.MinGapFrames, frameRate);
        var cues = new List<(Flicks Start, Flicks End, string Text)>();
        for (int index = 0; index < groups.Count; index++)
        {
            List<(string Text, Flicks Start, Flicks End)> group = groups[index];
            string text = string.Join('\n', Lines([.. group.Select(item => item.Text.Trim())], rules) ?? [string.Join(' ', group.Select(item => item.Text.Trim()))]);
            Flicks start = OnFrame(group[0].Start, frameRate, RoundingMode.Floor);
            if (cues.Count > 0 && start < cues[^1].End + minGap)
            {
                start = cues[^1].End + minGap;
            }

            // Up for its words, and slow enough to read, as far as the next cue allows.
            Flicks readable = Flicks.Max(rules.Shortest, Flicks.FromSeconds(text.Length / rules.MaxCharsPerSecond));
            Flicks end = OnFrame(Flicks.Max(group[^1].End, start + readable), frameRate, RoundingMode.Ceiling);
            if (index + 1 < groups.Count)
            {
                Flicks next = OnFrame(groups[index + 1][0].Start, frameRate, RoundingMode.Floor);
                if (next - end < CloseUp || end > next - minGap)
                {
                    end = next - minGap;
                }
            }

            // Never too short to see: the next cue waits instead.
            end = Flicks.Max(end, OnFrame(start + rules.Shortest, frameRate, RoundingMode.Ceiling));
            cues.Add((start, end, text));
        }

        return cues;
    }

    /// <summary>Words cut at sentence ends and at pauses.</summary>
    private static List<List<(string Text, Flicks Start, Flicks End)>> Phrases(IReadOnlyList<(string Text, Flicks Start, Flicks End)> words)
    {
        var phrases = new List<List<(string Text, Flicks Start, Flicks End)>>();
        var current = new List<(string Text, Flicks Start, Flicks End)>();
        foreach ((string Text, Flicks Start, Flicks End) word in words.Where(word => word.Text.Trim().Length > 0))
        {
            if (current.Count > 0)
            {
                string said = current[^1].Text.TrimEnd();
                bool sentence = said.EndsWith('.') || said.EndsWith('?') || said.EndsWith('!');
                if (sentence || word.Start - current[^1].End >= PauseBreak)
                {
                    phrases.Add(current);
                    current = [];
                }
            }

            current.Add(word);
        }

        if (current.Count > 0)
        {
            phrases.Add(current);
        }

        return phrases;
    }

    /// <summary>
    /// A phrase as few cues as hold it, each fitting its lines and up no longer than the longest,
    /// with the longest of them as short as it can be.
    /// </summary>
    private static IEnumerable<List<(string Text, Flicks Start, Flicks End)>> Share(List<(string Text, Flicks Start, Flicks End)> phrase, CueRules rules)
    {
        int count = phrase.Count;
        bool Fits(int from, int to) =>
            (to - from == 1 || phrase[to - 1].End - phrase[from].Start <= rules.Longest)
            && Lines([.. phrase.Skip(from).Take(to - from).Select(item => item.Text.Trim())], rules) is not null;
        int Length(int from, int to) => phrase.Skip(from).Take(to - from).Sum(item => item.Text.Trim().Length + 1) - 1;

        // best[i]: the fewest cues for the first i words, then the shortest longest cue.
        var best = new (int Cues, int Longest, int From)[count + 1];
        best[0] = (0, 0, 0);
        for (int to = 1; to <= count; to++)
        {
            best[to] = (int.MaxValue, int.MaxValue, to - 1);
            for (int from = to - 1; from >= 0; from--)
            {
                if (best[from].Cues == int.MaxValue || !Fits(from, to))
                {
                    if (to - from > 1)
                    {
                        break;
                    }

                    continue;
                }

                (int Cues, int Longest, int From) candidate = (best[from].Cues + 1, Math.Max(best[from].Longest, Length(from, to)), from);
                if (candidate.Cues < best[to].Cues || (candidate.Cues == best[to].Cues && candidate.Longest < best[to].Longest))
                {
                    best[to] = candidate;
                }
            }
        }

        var cuts = new List<(int From, int To)>();
        for (int to = count; to > 0; to = best[to].From)
        {
            cuts.Add((best[to].From, to));
        }

        cuts.Reverse();
        return cuts.Select(cut => phrase.GetRange(cut.From, cut.To - cut.From));
    }

    private static Flicks OnFrame(Flicks time, Rational frameRate, RoundingMode rounding) =>
        Flicks.FromFrames(time.ToFrames(frameRate, rounding), frameRate);

    /// <summary>
    /// Words as lines: one line when they fit on one, else two lines as near the same length as
    /// they go, else as many full lines as it takes; null when that is more than the rules allow.
    /// A word longer than a line has a line to itself.
    /// </summary>
    public static IReadOnlyList<string>? Lines(IReadOnlyList<string> words, CueRules rules)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(rules);
        string all = string.Join(' ', words);
        if (all.Length <= rules.MaxChars || words.Count == 1)
        {
            return [all];
        }

        // Greedy fill says how many lines it takes.
        var greedy = new List<string>();
        string line = string.Empty;
        foreach (string word in words)
        {
            string longer = line.Length == 0 ? word : line + " " + word;
            if (longer.Length <= rules.MaxChars || line.Length == 0)
            {
                line = longer;
            }
            else
            {
                greedy.Add(line);
                line = word;
            }
        }

        greedy.Add(line);
        if (greedy.Count > rules.MaxLines)
        {
            return null;
        }

        if (greedy.Count == 2)
        {
            // The split whose longer line is shortest.
            (string First, string Second)? best = null;
            for (int split = 1; split < words.Count; split++)
            {
                string first = string.Join(' ', words.Take(split));
                string second = string.Join(' ', words.Skip(split));
                if (first.Length <= rules.MaxChars && second.Length <= rules.MaxChars
                    && (best is null || Math.Max(first.Length, second.Length) < Math.Max(best.Value.First.Length, best.Value.Second.Length)))
                {
                    best = (first, second);
                }
            }

            if (best is { } balanced)
            {
                return [balanced.First, balanced.Second];
            }
        }

        return greedy;
    }
}
