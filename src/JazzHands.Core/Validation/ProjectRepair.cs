using System.Collections.Immutable;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Validation;

/// <summary>One change a repair made.</summary>
/// <param name="Code">The validation code it was fixing.</param>
/// <param name="Path">Where in the project it happened.</param>
/// <param name="Message">What was done, in a sentence.</param>
public sealed record RepairAction(string Code, string Path, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{Path}: {Code}: {Message}";
}

/// <summary>What a repair produced.</summary>
/// <param name="Project">The project after repair.</param>
/// <param name="Actions">Everything that was changed.</param>
/// <param name="Remaining">Problems the repair would not touch.</param>
public sealed record RepairResult(
    Project Project,
    ImmutableArray<RepairAction> Actions,
    ImmutableArray<ValidationIssue> Remaining)
{
    /// <summary>True when nothing needed doing.</summary>
    public bool IsClean => Actions.IsEmpty;
}

/// <summary>
/// Makes a broken project loadable again, conservatively.
/// </summary>
/// <remarks>
/// Only the problems with one obvious answer are touched: a reference to something that is not
/// there, a speed of zero, a fade longer than the clip it is on, a clip that starts before the
/// timeline does. Everything else is reported and left alone.
///
/// The line is deliberate. Two clips overlapping on a track could be fixed by trimming either
/// one, by moving one, or by deleting one, and a tool that picks for you will eventually pick
/// wrong and throw away the take you wanted. Repair exists so that a file with a typo in it opens
/// at all, not so that it edits the project on the user's behalf.
/// </remarks>
public static class ProjectRepair
{
    /// <summary>Repairs what can be repaired and reports the rest.</summary>
    public static RepairResult Apply(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var actions = ImmutableArray.CreateBuilder<RepairAction>();
        Project repaired = project;

        var mediaIds = new HashSet<string>(repaired.Media.Select(item => item.Id), StringComparer.Ordinal);
        var sequenceIds = new HashSet<string>(repaired.Sequences.Select(item => item.Id), StringComparer.Ordinal);

        var sequences = ImmutableArray.CreateBuilder<Sequence>(repaired.Sequences.Length);
        for (int sequenceIndex = 0; sequenceIndex < repaired.Sequences.Length; sequenceIndex++)
        {
            sequences.Add(RepairSequence(
                repaired.Sequences[sequenceIndex],
                $"/sequences/{sequenceIndex}",
                mediaIds,
                sequenceIds,
                actions));
        }

        repaired = repaired with { Sequences = new EquatableArray<Sequence>(sequences.ToImmutable()) };
        repaired = RepairActiveSequence(repaired, actions);

        return new RepairResult(
            repaired,
            actions.ToImmutable(),
            Validator.Semantic(repaired));
    }

    private static Project RepairActiveSequence(Project project, ImmutableArray<RepairAction>.Builder actions)
    {
        if (project.ActiveSequenceId is not null && project.Sequence(project.ActiveSequenceId) is not null)
        {
            return project;
        }

        if (project.Sequences.IsEmpty)
        {
            return project;
        }

        string first = project.Sequences[0].Id;
        actions.Add(new RepairAction(
            "missing-active-sequence",
            "/activeSequenceId",
            $"Pointed the active sequence at '{project.Sequences[0].Name}', because it pointed at a sequence that is not in the project."));

        return project with { ActiveSequenceId = first };
    }

    private static Sequence RepairSequence(
        Sequence sequence,
        string path,
        HashSet<string> mediaIds,
        HashSet<string> sequenceIds,
        ImmutableArray<RepairAction>.Builder actions)
    {
        var tracks = ImmutableArray.CreateBuilder<Track>(sequence.Tracks.Length);

        for (int trackIndex = 0; trackIndex < sequence.Tracks.Length; trackIndex++)
        {
            tracks.Add(RepairTrack(
                sequence.Tracks[trackIndex],
                $"{path}/tracks/{trackIndex}",
                mediaIds,
                sequenceIds,
                actions));
        }

        if (sequence.QuickTrim is { } trim && !mediaIds.Contains(trim.MediaId))
        {
            actions.Add(new RepairAction(
                "missing-trim-media",
                $"{path}/quickTrim",
                $"'{sequence.Name}' was a Quick Trim of media that is gone; it is now an ordinary sequence."));
            sequence = sequence with { QuickTrim = null };
        }

        return sequence with { Tracks = new EquatableArray<Track>(tracks.ToImmutable()) };
    }

    private static Track RepairTrack(
        Track track,
        string path,
        HashSet<string> mediaIds,
        HashSet<string> sequenceIds,
        ImmutableArray<RepairAction>.Builder actions)
    {
        var clips = ImmutableArray.CreateBuilder<Clip>(track.Clips.Length);

        for (int clipIndex = 0; clipIndex < track.Clips.Length; clipIndex++)
        {
            Clip clip = track.Clips[clipIndex];
            string clipPath = $"{path}/clips/{clipIndex}";

            if (IsOrphan(clip, mediaIds, sequenceIds, out string? reason))
            {
                actions.Add(new RepairAction(
                    clip.MediaId is not null ? "missing-media-reference" : "missing-sequence-reference",
                    clipPath,
                    $"Removed the clip '{Describe(clip)}': {reason}"));
                continue;
            }

            clips.Add(RepairClip(clip, clipPath, actions));
        }

        Track repaired = track with { Clips = new EquatableArray<Clip>(clips.ToImmutable()) };
        return RepairTransitions(repaired, path, actions);
    }

    private static Track RepairTransitions(Track track, string path, ImmutableArray<RepairAction>.Builder actions)
    {
        if (track.Transitions.IsEmpty)
        {
            return track;
        }

        var clipIds = new HashSet<string>(track.Clips.Select(clip => clip.Id), StringComparer.Ordinal);
        var kept = ImmutableArray.CreateBuilder<Transition>(track.Transitions.Length);

        for (int index = 0; index < track.Transitions.Length; index++)
        {
            Transition transition = track.Transitions[index];

            if (!clipIds.Contains(transition.LeftClipId) || !clipIds.Contains(transition.RightClipId))
            {
                actions.Add(new RepairAction(
                    "missing-transition-clip",
                    $"{path}/transitions/{index}",
                    $"Removed the transition '{transition.TypeId}': one of the clips it joins is not on the track."));
                continue;
            }

            kept.Add(transition);
        }

        return kept.Count == track.Transitions.Length
            ? track
            : track with { Transitions = new EquatableArray<Transition>(kept.ToImmutable()) };
    }

    private static Clip RepairClip(Clip clip, string path, ImmutableArray<RepairAction>.Builder actions)
    {
        Clip repaired = clip;

        if (repaired.Speed is { IsZero: true })
        {
            actions.Add(new RepairAction(
                "zero-speed",
                $"{path}/speed",
                "Set the speed to 1/1, because a clip at zero speed would never advance."));
            repaired = repaired with { Speed = Rational.One };
        }

        if (repaired.Range.Start < Flicks.Zero)
        {
            actions.Add(new RepairAction(
                "negative-start",
                $"{path}/range/start",
                $"Moved the clip from {repaired.Range.Start.Value} flicks to the start of the timeline."));
            repaired = repaired with { Range = new TimeRange(Flicks.Zero, repaired.Range.Duration) };
        }

        if (repaired.SourceIn < Flicks.Zero)
        {
            actions.Add(new RepairAction(
                "negative-source-in",
                $"{path}/sourceIn",
                "Moved the source start to the beginning of the media, because it was before it."));
            repaired = repaired with { SourceIn = Flicks.Zero };
        }

        repaired = ClampFade(repaired, path, actions);
        return repaired;
    }

    /// <summary>
    /// Shortens fades that are longer than the clip carrying them.
    /// </summary>
    /// <remarks>
    /// Both together are clamped to the clip's duration, so a clip with a two second fade in and
    /// a two second fade out on a three second clip ends up with one and a half of each rather
    /// than with a fade that runs past the end.
    /// </remarks>
    private static Clip ClampFade(Clip clip, string path, ImmutableArray<RepairAction>.Builder actions)
    {
        Flicks fadeIn = clip.FadeIn?.Duration ?? Flicks.Zero;
        Flicks fadeOut = clip.FadeOut?.Duration ?? Flicks.Zero;

        if (fadeIn + fadeOut <= clip.Duration || clip.Duration.IsZero)
        {
            return clip;
        }

        long total = fadeIn.Value + fadeOut.Value;
        var scaledIn = new Flicks((long)((Int128)fadeIn.Value * clip.Duration.Value / total));
        var scaledOut = new Flicks(clip.Duration.Value - scaledIn.Value);

        actions.Add(new RepairAction(
            "fade-longer-than-clip",
            $"{path}/fadeIn",
            "Shortened the fades so that together they fit inside the clip."));

        return clip with
        {
            FadeIn = clip.FadeIn is null ? null : clip.FadeIn with { Duration = scaledIn },
            FadeOut = clip.FadeOut is null ? null : clip.FadeOut with { Duration = scaledOut },
        };
    }

    private static bool IsOrphan(Clip clip, HashSet<string> mediaIds, HashSet<string> sequenceIds, out string? reason)
    {
        if (clip.MediaId is { } mediaId && !mediaIds.Contains(mediaId))
        {
            reason = $"it plays media '{mediaId}', which is not in the project.";
            return true;
        }

        if (clip.SequenceId is { } sequenceId && !sequenceIds.Contains(sequenceId))
        {
            reason = $"it nests sequence '{sequenceId}', which is not in the project.";
            return true;
        }

        reason = null;
        return false;
    }

    private static string Describe(Clip clip) => clip.Name.Length > 0 ? clip.Name : clip.Id;
}
