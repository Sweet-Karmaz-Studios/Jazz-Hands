using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Queries;
using JazzHands.Core.Time;
using JazzHands.Core.Validation;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>
/// Describes a sequence in text, for a person or a model to read.
/// </summary>
/// <remarks>
/// This is how Claude Code sees an edit, so it is written to be read rather than parsed: no box
/// drawing, no tables, times as timecode at the sequence rate, and the things that are wrong said
/// out loud rather than left to be worked out. A brief description of a typical trailer is a few
/// hundred tokens, which is the point: it can be asked for before and after every change.
///
/// Anything that needs to be machine-read should use the other queries, which return records.
/// </remarks>
public sealed class DescribeTimelineHandler : IQueryHandler<DescribeTimelineQuery, string>
{
    /// <inheritdoc />
    public string Handle(Project project, DescribeTimelineQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);

        Sequence sequence = HandlerHelp.Sequence(project, query.SequenceId);
        ProjectSettings settings = project.SettingsFor(sequence);
        Rational fps = settings.FrameRate;
        bool full = query.Detail == DescribeDetail.Full;

        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"{sequence.Name} ({sequence.Id})\n");
        text.Append(CultureInfo.InvariantCulture, $"  {settings.Width}x{settings.Height} at {fps} fps");
        text.Append(CultureInfo.InvariantCulture, $", {settings.SampleRate} Hz {Channels(settings.ChannelCount)}");
        text.Append(CultureInfo.InvariantCulture, $", {settings.ColorSpace}");
        text.Append(sequence.Settings is null ? " (from the project)\n" : " (its own)\n");
        text.Append(CultureInfo.InvariantCulture, $"  {Timecode.Format(sequence.Duration, fps)} long");
        text.Append(CultureInfo.InvariantCulture, $", {sequence.Tracks.Length} tracks");
        text.Append(CultureInfo.InvariantCulture, $", {sequence.Tracks.Sum(track => track.Clips.Length)} clips\n");

        if (sequence.InOut is { } range)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"  in and out: {Timecode.Format(range.Start, fps)} to {Timecode.Format(range.End, fps)}\n");
        }

        // The order a timeline is drawn in: picture stacked upward from the middle, sound
        // downward. Reading V2, V1, A1, A2 is what somebody looking at the editor would see.
        IEnumerable<Track> picture = sequence.Tracks
            .Where(track => track.Kind is TrackKind.Video or TrackKind.Adjustment)
            .OrderByDescending(track => track.Order);

        IEnumerable<Track> sound = sequence.Tracks
            .Where(track => track.Kind is TrackKind.Audio or TrackKind.Subtitle)
            .OrderBy(track => track.Order);

        foreach (Track track in picture.Concat(sound))
        {
            AppendTrack(text, project, sequence, track, fps, full);
        }

        AppendMarkers(text, sequence, fps);

        if (full)
        {
            AppendProblems(text, project, sequence);
        }

        return text.ToString();
    }

    private static void AppendTrack(
        StringBuilder text,
        Project project,
        Sequence sequence,
        Track track,
        Rational fps,
        bool full)
    {
        text.Append('\n');
        text.Append(CultureInfo.InvariantCulture,
            $"{track.Name} ({track.Kind.ToString().ToLowerInvariant()}, order {track.Order})");

        string[] flags =
        [
            .. new[]
            {
                track.Locked ? "locked" : null,
                track.Muted ? "muted" : null,
                track.Solo ? "solo" : null,
                TimelineQueries.IsAudible(sequence, track) ? null : "silent",
            }.Where(flag => flag is not null).Select(flag => flag!),
        ];

        if (flags.Length > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $" [{string.Join(", ", flags)}]");
        }

        text.Append('\n');

        if (track.Clips.IsEmpty)
        {
            text.Append("  empty\n");
            return;
        }

        foreach (Clip clip in track.Clips)
        {
            AppendClip(text, project, clip, fps, full, track.IsAudio);
        }

        foreach (Gap gap in TimelineQueries.Gaps(track))
        {
            text.Append(CultureInfo.InvariantCulture,
                $"  gap    {Timecode.Format(gap.Range.Start, fps)} to {Timecode.Format(gap.Range.End, fps)}\n");
        }

        if (full && !track.Transitions.IsEmpty)
        {
            foreach (Transition transition in track.Transitions)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"  transition {transition.TypeId} {Timecode.Format(transition.Duration, fps)}"
                    + $" between {Short(transition.LeftClipId)} and {Short(transition.RightClipId)}\n");
            }
        }
    }

    private static void AppendClip(StringBuilder text, Project project, Clip clip, Rational fps, bool full, bool audio)
    {
        text.Append(CultureInfo.InvariantCulture,
            $"  {Timecode.Format(clip.Start, fps)} {Timecode.Format(clip.Duration, fps)} {Name(clip)}");

        string[] notes =
        [
            .. new[]
            {
                clip.Enabled ? null : "disabled",
                clip.Reverse ? "reversed" : null,
                clip.EffectiveSpeed == Rational.One ? null : $"{clip.EffectiveSpeed}x",
                clip.LinkGroupId is null ? null : "linked",
                clip.GroupId is null ? null : "grouped",
            }.Where(note => note is not null).Select(note => note!),
            .. (audio ? AudioNotes(clip) : []),
        ];

        if (notes.Length > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $" [{string.Join(", ", notes)}]");
        }

        text.Append('\n');

        if (!full)
        {
            return;
        }

        text.Append(CultureInfo.InvariantCulture, $"         id {clip.Id}, source {Source(project, clip)}");
        text.Append(CultureInfo.InvariantCulture,
            $", in {Timecode.Format(clip.SourceIn, fps)} out {Timecode.Format(clip.SourceOut, fps)}\n");

        foreach (Effect effect in clip.Effects)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"         effect {effect.TypeId}{(effect.Enabled ? string.Empty : " (bypassed)")}\n");
        }

        foreach (Marker marker in clip.Markers)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"         marker {Timecode.Format(clip.Start + marker.Time, fps)} {marker.Name}\n");
        }
    }

    private static void AppendMarkers(StringBuilder text, Sequence sequence, Rational fps)
    {
        if (sequence.Markers.IsEmpty)
        {
            return;
        }

        text.Append("\nmarkers\n");

        foreach (Marker marker in sequence.Markers)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {Timecode.Format(marker.Time, fps)} {marker.Name}");

            if (marker.IsRange)
            {
                text.Append(CultureInfo.InvariantCulture, $" (for {Timecode.Format(marker.Duration, fps)})");
            }

            if (marker.IsChapter)
            {
                text.Append(" [chapter]");
            }

            text.Append('\n');
        }
    }

    private static void AppendProblems(StringBuilder text, Project project, Sequence sequence)
    {
        ImmutableArray<ValidationIssue> issues =
        [
            .. Validator.Semantic(project)
                .Where(issue => issue.Path.Contains($"/sequences/{IndexOf(project, sequence)}/", StringComparison.Ordinal)
                    || issue.Path == "/"),
        ];

        if (issues.IsEmpty)
        {
            return;
        }

        text.Append("\nproblems\n");

        foreach (ValidationIssue issue in issues)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"  {issue.Severity.ToString().ToLowerInvariant()}: {issue.Code}: {issue.Message}\n");
        }
    }

    private static int IndexOf(Project project, Sequence sequence)
    {
        for (int index = 0; index < project.Sequences.Length; index++)
        {
            if (string.Equals(project.Sequences[index].Id, sequence.Id, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// What is set on an audio clip, in the words a mixer would use, so a reader can tell the
    /// mic lane is at -6 with a half second fade without opening the project file.
    /// </summary>
    private static IEnumerable<string> AudioNotes(Clip clip)
    {
        if (clip.IsMedia)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"stream {clip.SourceStreamIndex}");
        }

        if (clip.Volume is not null)
        {
            yield return clip.Volume is StaticValue { Value: ParamValue.Float gain }
                ? string.Create(CultureInfo.InvariantCulture, $"gain {gain.Value:+0.#;-0.#} dB")
                : "gain automated";
        }

        if (clip.Pan is not null)
        {
            yield return clip.Pan is StaticValue { Value: ParamValue.Float pan }
                ? string.Create(CultureInfo.InvariantCulture, $"pan {pan.Value:+0.##;-0.##}")
                : "pan automated";
        }

        if (clip.FadeIn is { IsNone: false } fadeIn)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"fade in {fadeIn.Duration.ToSeconds():0.###} s");
        }

        if (clip.FadeOut is { IsNone: false } fadeOut)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"fade out {fadeOut.Duration.ToSeconds():0.###} s");
        }

        if (clip.ChannelMap is { } map and not AudioChannelMap.Auto)
        {
            yield return map == AudioChannelMap.Mono ? "summed to mono" : $"{map.ToString().ToLowerInvariant()} channel only";
        }
    }

    private static string Name(Clip clip) => clip.Name.Length > 0 ? clip.Name : Short(clip.Id);

    private static string Source(Project project, Clip clip) =>
        clip.MediaId is { } mediaId
            ? project.MediaItem(mediaId) is { } media ? $"media {media.Name}" : $"media {Short(mediaId)} (missing)"
        : clip.SequenceId is { } nestedId
            ? project.Sequence(nestedId) is { } nested ? $"sequence {nested.Name}" : $"sequence {Short(nestedId)} (missing)"
        : clip.GeneratorId is { } generator ? $"generator {generator}"
        : "nothing";

    private static string Channels(int count) => count switch
    {
        1 => "mono",
        2 => "stereo",
        6 => "5.1",
        _ => $"{count} channels",
    };

    /// <summary>The last six characters of an identifier, which is enough to tell two apart.</summary>
    private static string Short(string id) => id.Length <= 6 ? id : id[^6..];
}
