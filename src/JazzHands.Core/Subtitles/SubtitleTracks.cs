using System.Collections.Immutable;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Core.Titles;

namespace JazzHands.Core.Subtitles;

/// <summary>Between a subtitle track's cue clips and a subtitle document.</summary>
public static class SubtitleTracks
{
    /// <summary>A cue as a clip on a subtitle track.</summary>
    public static Clip Clip(string id, Flicks start, Flicks duration, Cue cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        return new Clip(id, new TimeRange(start, duration), Flicks.Zero, GeneratorId: Cue.GeneratorId, Name: Label(cue.Text), Cue: cue);
    }

    /// <summary>A short name for a cue on the timeline: its first line, as it reads.</summary>
    public static string Label(string markup)
    {
        string plain = TitleMarkup.PlainText(markup).Replace('\r', ' ');
        string first = plain.Split('\n')[0].Trim();
        return first.Length <= 40 ? first : first[..39] + "…";
    }

    /// <summary>
    /// A subtitle track's cues as a document, at their sequence times, or at the times an export
    /// of <paramref name="ranges"/> played back to back puts them.
    /// </summary>
    /// <remarks>
    /// Against ranges, a cue is cut to each range it overlaps and moved by where that range lands
    /// in the output; a cue outside every range is left out, one across a cut becomes two.
    /// </remarks>
    public static SubtitleDocument Document(Track track, IReadOnlyList<TimeRange>? ranges = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        var cues = new List<SubtitleCue>();

        foreach (Clip clip in track.Clips)
        {
            if (clip.Cue is not { } cue || !clip.Enabled)
            {
                continue;
            }

            if (ranges is null)
            {
                cues.Add(new SubtitleCue(clip.Start, clip.End, cue.Text, cue.Align, Name: cue.Name, Raw: cue.Raw));
                continue;
            }

            Flicks offset = Flicks.Zero;
            foreach (TimeRange range in ranges)
            {
                Flicks start = Flicks.Max(clip.Start, range.Start);
                Flicks end = Flicks.Min(clip.End, range.End);
                if (end > start)
                {
                    cues.Add(new SubtitleCue(start - range.Start + offset, end - range.Start + offset, cue.Text, cue.Align, Name: cue.Name, Raw: cue.Raw));
                }

                offset += range.Duration;
            }
        }

        return new SubtitleDocument(SubtitleFiles.Ordered(cues), track.SubtitleStyle ?? SubtitleStyle.Default);
    }

    /// <summary>
    /// A document's cues as clips, moved by an offset: cues with no length, or that would start
    /// before the timeline, are left out.
    /// </summary>
    public static ImmutableArray<Clip> Clips(SubtitleDocument document, Flicks offset)
    {
        ArgumentNullException.ThrowIfNull(document);
        return [.. document.Cues
            .Where(cue => cue.End > cue.Start && cue.Start + offset >= Flicks.Zero)
            .Select(cue => Clip(Id.New(), cue.Start + offset, cue.Duration, new Cue(cue.Text, cue.Align, cue.Name, cue.Raw)))];
    }
}
