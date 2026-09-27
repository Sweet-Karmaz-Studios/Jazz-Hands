using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Speech;

/// <summary>A word as a clip plays it: its times on the timeline.</summary>
/// <param name="Index">Its place among the clip's words, from 0: what <c>clip.remove-words</c> takes.</param>
/// <param name="ClipId">The clip.</param>
/// <param name="Text">The word as written.</param>
/// <param name="Start">When it begins on the timeline.</param>
/// <param name="End">When it ends on the timeline.</param>
/// <param name="Confidence">How sure the model was, 0 to 1.</param>
/// <param name="Filler">True for a filler word (um, uh and the like).</param>
public sealed record TimelineWord(int Index, string ClipId, string Text, Flicks Start, Flicks End, float Confidence, bool Filler);

/// <summary>A stretch of a clip a clean-up would take out.</summary>
/// <param name="From">Where it starts on the timeline, on a frame.</param>
/// <param name="To">Where it ends on the timeline, on a frame.</param>
/// <param name="Reason"><c>filler</c> or <c>pause</c>.</param>
/// <param name="Words">The words it takes out, as said.</param>
public sealed record SpeechCut(Flicks From, Flicks To, string Reason, string Words);

/// <summary>
/// The words of a transcript as a clip plays them (Phase 39): only those inside its in and out,
/// at the times its speed and speed curve put them on the timeline. A transcript is of the whole
/// file, so every clip of it shares one.
/// </summary>
public static class ClipWords
{
    /// <summary>
    /// The words a clip plays, in order: each one whose middle falls inside the clip's source range,
    /// at the timeline times its start and end are shown. A reversed clip or a freeze frame plays
    /// no words (backwards speech is not speech).
    /// </summary>
    public static IReadOnlyList<TimelineWord> Of(Clip clip, Transcript transcript, IReadOnlyCollection<string>? fillers = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(transcript);
        if (clip.Reverse || clip.IsHold || clip.Duration <= Flicks.Zero)
        {
            return [];
        }

        HashSet<string> filler = new(fillers ?? TranscribedWord.DefaultFillers, StringComparer.OrdinalIgnoreCase);
        Flicks sourceIn = clip.SourceIn;
        Flicks sourceOut = clip.SourceOut;
        var words = new List<TimelineWord>();
        foreach (TranscribedWord word in transcript.Words)
        {
            Flicks middle = word.Start + ((word.End - word.Start) / 2);
            if (middle < sourceIn || middle >= sourceOut)
            {
                continue;
            }

            Flicks start = TimelineTimeOf(clip, Flicks.Max(word.Start, sourceIn));
            Flicks end = TimelineTimeOf(clip, Flicks.Min(word.End, sourceOut));
            words.Add(new TimelineWord(words.Count, clip.Id, word.Text, start, Flicks.Max(start, end), word.Confidence, filler.Contains(word.Bare)));
        }

        return words;
    }

    /// <summary>
    /// The timeline time at which a clip shows a source time: the inverse of
    /// <see cref="Clip.SourceTimeAt"/>, found by halving for a speed curve, clamped to the clip.
    /// </summary>
    public static Flicks TimelineTimeOf(Clip clip, Flicks source)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (source <= clip.SourceIn)
        {
            return clip.Start;
        }

        if (source >= clip.SourceOut)
        {
            return clip.End;
        }

        if (!clip.IsRemapped)
        {
            // Exact for one speed: offset over speed, in rationals.
            Rational speed = clip.EffectiveSpeed;
            Int128 offset = (Int128)(source - clip.SourceIn).Value * speed.Den / speed.Num;
            return Flicks.Min(clip.End, clip.Start + new Flicks((long)offset));
        }

        long low = clip.Start.Value;
        long high = clip.End.Value;
        while (high - low > 1)
        {
            long middle = low + ((high - low) / 2);
            if (clip.SourceTimeAt(new Flicks(middle)) < source)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return new Flicks(high);
    }

    /// <summary>
    /// The stretch deleting words <paramref name="from"/> to <paramref name="to"/> takes out: from
    /// the first one's start to the start of the word after the last (so the pause after them goes
    /// with them, and what is left reads on without a doubled pause), or to the last one's end when
    /// it is the clip's last word; each edge on the nearest frame.
    /// </summary>
    public static (Flicks From, Flicks To) Stretch(IReadOnlyList<TimelineWord> words, int from, int to, Clip clip, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(clip);
        Flicks start = words[from].Start;
        Flicks end = to + 1 < words.Count ? words[to + 1].Start : words[to].End;
        return (Snap(Flicks.Max(start, clip.Start), frameRate), Snap(Flicks.Min(end, clip.End), frameRate));
    }

    /// <summary>
    /// What a clean-up of a clip would take out: every filler word with the pause after it, and every
    /// pause between words longer than <paramref name="longPause"/> cut down to
    /// <paramref name="keep"/> (half of it left each side). Stretches that touch are joined; each
    /// edge is on the nearest frame; in timeline order.
    /// </summary>
    /// <param name="words">The clip's words.</param>
    /// <param name="clip">The clip.</param>
    /// <param name="frameRate">The sequence's frame rate, for the frames the edges snap to.</param>
    /// <param name="removeFillers">Take out the filler words.</param>
    /// <param name="longPause">A pause longer than this is shortened; null leaves pauses alone.</param>
    /// <param name="keep">How much of a long pause is left.</param>
    public static IReadOnlyList<SpeechCut> CleanUp(IReadOnlyList<TimelineWord> words, Clip clip, Rational frameRate, bool removeFillers, Flicks? longPause, Flicks keep)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(clip);
        var cuts = new List<(Flicks From, Flicks To, string Reason, string Words)>();

        for (int index = 0; index < words.Count; index++)
        {
            TimelineWord word = words[index];
            if (removeFillers && word.Filler)
            {
                // A run of fillers ("um, uh") goes as one.
                int last = index;
                while (last + 1 < words.Count && words[last + 1].Filler)
                {
                    last++;
                }

                (Flicks from, Flicks to) = Stretch(words, index, last, clip, frameRate);
                cuts.Add((from, to, "filler", string.Join(' ', words.Skip(index).Take(last - index + 1).Select(item => item.Text.Trim()))));
                index = last;
                continue;
            }

            if (longPause is { } threshold && index + 1 < words.Count && !(removeFillers && words[index + 1].Filler))
            {
                Flicks gap = words[index + 1].Start - word.End;
                if (gap > threshold && gap > keep)
                {
                    Flicks half = keep / 2;
                    cuts.Add((Snap(word.End + half, frameRate), Snap(words[index + 1].Start - (keep - half), frameRate), "pause", string.Empty));
                }
            }
        }

        // Join what touches or overlaps, and drop what snapped to nothing.
        var joined = new List<SpeechCut>();
        foreach ((Flicks from, Flicks to, string reason, string said) in cuts.OrderBy(cut => cut.From))
        {
            if (to <= from)
            {
                continue;
            }

            if (joined.Count > 0 && from <= joined[^1].To)
            {
                SpeechCut previous = joined[^1];
                joined[^1] = previous with
                {
                    To = Flicks.Max(previous.To, to),
                    Reason = previous.Reason == reason ? reason : "filler",
                    Words = string.Join(' ', new[] { previous.Words, said }.Where(text => text.Length > 0)),
                };
            }
            else
            {
                joined.Add(new SpeechCut(from, to, reason, said));
            }
        }

        return joined;
    }

    /// <summary>A time on the nearest frame of a rate.</summary>
    public static Flicks Snap(Flicks time, Rational frameRate) =>
        Flicks.FromFrames(time.ToFrames(frameRate, RoundingMode.Nearest), frameRate);
}
