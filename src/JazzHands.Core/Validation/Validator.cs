using System.Collections.Immutable;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Validation;

/// <summary>How much a validation issue matters.</summary>
public enum Severity
{
    /// <summary>Worth knowing. The project loads and edits normally.</summary>
    Warning,

    /// <summary>The project is inconsistent. The engine refuses to load it without a repair.</summary>
    Error,
}

/// <summary>
/// One thing wrong with a project.
/// </summary>
/// <param name="Severity">Whether this blocks loading.</param>
/// <param name="Code">A stable kebab-case code, so tools can match on it.</param>
/// <param name="Path">A JSON pointer into the project file, so an editor can jump to it.</param>
/// <param name="Message">A sentence a person can act on.</param>
public sealed record ValidationIssue(Severity Severity, string Code, string Path, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{Path}: {Code}: {Message}";
}

/// <summary>
/// Checks a project for the things a schema cannot express.
/// </summary>
/// <remarks>
/// The .jazz file is meant to be hand-edited, by a person or by Claude Code, so this has to
/// explain what is wrong and where rather than refusing to open the file. Paths are JSON
/// pointers into the document, which is what lets the CLI print something clickable and the Log
/// panel jump to it.
///
/// Schema validation happens first and separately, in Phase 04; this is the semantic layer.
/// </remarks>
public static class Validator
{
    /// <summary>Runs every semantic check over a project.</summary>
    public static ImmutableArray<ValidationIssue> Semantic(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();

        CheckProject(project, issues);

        var mediaIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (MediaItem media in project.Media)
        {
            mediaIds.Add(media.Id);
        }

        var sequenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (Sequence sequence in project.Sequences)
        {
            sequenceIds.Add(sequence.Id);
        }

        for (int sequenceIndex = 0; sequenceIndex < project.Sequences.Length; sequenceIndex++)
        {
            CheckSequence(project.Sequences[sequenceIndex], sequenceIndex, mediaIds, sequenceIds, issues);
        }

        CheckSequenceCycles(project, issues);

        return issues.ToImmutable();
    }

    /// <summary>
    /// Checks a parsed document's shape, before anything is deserialized.
    /// </summary>
    /// <remarks>
    /// The two layers are separate because they fail at different times and say different things.
    /// This one catches a string where a number belongs and reports the pointer to it; the
    /// semantic layer catches two clips on top of each other, which no schema can express.
    /// </remarks>
    public static ImmutableArray<ValidationIssue> Schema(System.Text.Json.Nodes.JsonNode? document) =>
        SchemaValidator.Check(document);

    /// <summary>True when nothing found blocks loading.</summary>
    public static bool IsLoadable(IEnumerable<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        return !issues.Any(issue => issue.Severity == Severity.Error);
    }

    private static void CheckProject(Project project, ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (!Id.IsValid(project.Id))
        {
            issues.Add(new ValidationIssue(
                Severity.Error,
                "invalid-id",
                "/id",
                $"'{project.Id}' is not a ULID. Use 'jazz ids new' for a fresh one."));
        }

        if (project.SchemaVersion is < 1 or > Project.CurrentSchemaVersion)
        {
            issues.Add(new ValidationIssue(
                Severity.Error,
                "unknown-schema-version",
                "/schemaVersion",
                $"Schema version {project.SchemaVersion} is not one this build understands "
                + $"(1 to {Project.CurrentSchemaVersion})."));
        }

        if (project.Settings.Width <= 0 || project.Settings.Height <= 0)
        {
            issues.Add(new ValidationIssue(
                Severity.Error,
                "invalid-frame-size",
                "/settings",
                $"Frame size {project.Settings.Size} is not a usable resolution."));
        }

        if (project.Settings.FrameRate.IsZero || project.Settings.FrameRate.Num < 0)
        {
            issues.Add(new ValidationIssue(
                Severity.Error,
                "invalid-frame-rate",
                "/settings/frameRate",
                $"Frame rate {project.Settings.FrameRate} is not positive."));
        }

        if (project.ActiveSequenceId is { } activeId && project.Sequence(activeId) is null)
        {
            issues.Add(new ValidationIssue(
                Severity.Warning,
                "missing-active-sequence",
                "/activeSequenceId",
                $"The active sequence '{activeId}' does not exist; the first sequence will be shown instead."));
        }

        if (project.Sequences.IsEmpty)
        {
            issues.Add(new ValidationIssue(
                Severity.Warning,
                "no-sequences",
                "/sequences",
                "The project has no sequences, so there is nothing to edit."));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < project.Media.Length; index++)
        {
            MediaItem media = project.Media[index];
            if (!seen.Add(media.Id))
            {
                issues.Add(new ValidationIssue(
                    Severity.Error,
                    "duplicate-id",
                    $"/media/{index}/id",
                    $"Media id '{media.Id}' is used more than once."));
            }

            if (System.IO.Path.IsPathRooted(media.RelativePath))
            {
                issues.Add(new ValidationIssue(
                    Severity.Warning,
                    "absolute-media-path",
                    $"/media/{index}/relativePath",
                    $"'{media.RelativePath}' is an absolute path, so the project will not move between machines."));
            }
        }
    }

    private static void CheckSequence(
        Sequence sequence,
        int sequenceIndex,
        HashSet<string> mediaIds,
        HashSet<string> sequenceIds,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        string sequencePath = $"/sequences/{sequenceIndex}";

        if (sequence.QuickTrim is { } trim && !mediaIds.Contains(trim.MediaId))
        {
            issues.Add(new ValidationIssue(
                Severity.Warning,
                "missing-trim-media",
                $"{sequencePath}/quickTrim/mediaId",
                $"'{sequence.Name}' is a Quick Trim of media '{trim.MediaId}', which is not in the project. It opens as an ordinary sequence; 'jazz repair' drops the Quick Trim mark."));
        }

        var trackIds = new HashSet<string>(StringComparer.Ordinal);
        var clipIds = new HashSet<string>(StringComparer.Ordinal);

        for (int trackIndex = 0; trackIndex < sequence.Tracks.Length; trackIndex++)
        {
            Track track = sequence.Tracks[trackIndex];
            string trackPath = $"{sequencePath}/tracks/{trackIndex}";

            if (!trackIds.Add(track.Id))
            {
                issues.Add(new ValidationIssue(
                    Severity.Error,
                    "duplicate-id",
                    $"{trackPath}/id",
                    $"Track id '{track.Id}' is used more than once in this sequence."));
            }

            CheckTrack(track, trackPath, mediaIds, sequenceIds, clipIds, issues);
        }
    }

    private static void CheckTrack(
        Track track,
        string trackPath,
        HashSet<string> mediaIds,
        HashSet<string> sequenceIds,
        HashSet<string> clipIds,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        Flicks previousEnd = Flicks.MinValue;
        string? previousName = null;

        for (int clipIndex = 0; clipIndex < track.Clips.Length; clipIndex++)
        {
            Clip clip = track.Clips[clipIndex];
            string clipPath = $"{trackPath}/clips/{clipIndex}";

            if (!clipIds.Add(clip.Id))
            {
                issues.Add(new ValidationIssue(
                    Severity.Error,
                    "duplicate-id",
                    $"{clipPath}/id",
                    $"Clip id '{clip.Id}' is used more than once in this sequence."));
            }

            CheckClip(clip, clipPath, mediaIds, sequenceIds, issues);

            if (clip.Start < previousEnd)
            {
                issues.Add(new ValidationIssue(
                    Severity.Error,
                    "overlapping-clips",
                    $"{clipPath}/range",
                    $"'{clip.Name}' starts at {Timecode.FormatClock(clip.Start)}, before '{previousName}' ends "
                    + $"at {Timecode.FormatClock(previousEnd)}. Two clips cannot occupy the same time on a track."));
            }

            previousEnd = clip.End;
            previousName = clip.Name;
        }

        CheckTransitions(track, trackPath, issues);
    }

    private static void CheckClip(
        Clip clip,
        string clipPath,
        HashSet<string> mediaIds,
        HashSet<string> sequenceIds,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (!Id.IsValid(clip.Id))
        {
            issues.Add(new ValidationIssue(
                Severity.Error,
                "invalid-id",
                $"{clipPath}/id",
                $"'{clip.Id}' is not a ULID."));
        }

        int sources = (clip.MediaId is null ? 0 : 1) + (clip.GeneratorId is null ? 0 : 1) + (clip.SequenceId is null ? 0 : 1);
        if (sources == 0)
        {
            issues.Add(new ValidationIssue(
                Severity.Error,
                "clip-without-source",
                clipPath,
                $"'{clip.Name}' references no media, generator or sequence, so there is nothing to play."));
        }
        else if (sources > 1)
        {
            issues.Add(new ValidationIssue(
                Severity.Error,
                "clip-with-many-sources",
                clipPath,
                $"'{clip.Name}' references more than one source; a clip plays exactly one thing."));
        }

        if (clip.MediaId is { } mediaId && !mediaIds.Contains(mediaId))
        {
            issues.Add(new ValidationIssue(
                Severity.Error,
                "missing-media-reference",
                $"{clipPath}/mediaId",
                $"'{clip.Name}' references media '{mediaId}', which is not in the project."));
        }

        if (clip.SequenceId is { } sequenceId && !sequenceIds.Contains(sequenceId))
        {
            issues.Add(new ValidationIssue(
                Severity.Error,
                "missing-sequence-reference",
                $"{clipPath}/sequenceId",
                $"'{clip.Name}' nests sequence '{sequenceId}', which is not in the project."));
        }

        if (clip.Duration.Value <= 0)
        {
            issues.Add(new ValidationIssue(
                Severity.Error,
                "empty-clip",
                $"{clipPath}/range",
                $"'{clip.Name}' has no duration."));
        }

        if (clip.Start.IsNegative)
        {
            issues.Add(new ValidationIssue(
                Severity.Error,
                "negative-start",
                $"{clipPath}/range",
                $"'{clip.Name}' starts before the beginning of the sequence."));
        }

        if (clip.SourceIn.IsNegative)
        {
            issues.Add(new ValidationIssue(
                Severity.Error,
                "negative-source-in",
                $"{clipPath}/sourceIn",
                $"'{clip.Name}' starts before the beginning of its source."));
        }

        if (clip.Speed is { } speed && speed.IsZero)
        {
            issues.Add(new ValidationIssue(
                Severity.Error,
                "zero-speed",
                $"{clipPath}/speed",
                $"'{clip.Name}' plays at zero speed, which would never advance."));
        }

        if (!clip.FadeIn.IsNoneOrNull() && clip.FadeIn!.Duration > clip.Duration)
        {
            issues.Add(new ValidationIssue(
                Severity.Warning,
                "fade-longer-than-clip",
                $"{clipPath}/fadeIn",
                $"The fade in on '{clip.Name}' is longer than the clip and will be clamped."));
        }

        if (!clip.FadeOut.IsNoneOrNull() && clip.FadeOut!.Duration > clip.Duration)
        {
            issues.Add(new ValidationIssue(
                Severity.Warning,
                "fade-longer-than-clip",
                $"{clipPath}/fadeOut",
                $"The fade out on '{clip.Name}' is longer than the clip and will be clamped."));
        }

        CheckKeyframesWithin(clip, clipPath, issues);

        for (int effectIndex = 0; effectIndex < clip.Effects.Length; effectIndex++)
        {
            Effect effect = clip.Effects[effectIndex];
            string effectPath = $"{clipPath}/effects/{effectIndex}";

            if (string.IsNullOrWhiteSpace(effect.TypeId))
            {
                issues.Add(new ValidationIssue(
                    Severity.Error,
                    "effect-without-type",
                    $"{effectPath}/typeId",
                    $"An effect on '{clip.Name}' has no type."));
            }
        }
    }

    private static void CheckKeyframesWithin(
        Clip clip,
        string clipPath,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        foreach ((string name, AnimatedValue? value) in EnumerateClipParameters(clip))
        {
            if (value is not KeyframedValue keyframed || keyframed.Keyframes.IsEmpty)
            {
                continue;
            }

            if (keyframed.End > clip.Duration)
            {
                issues.Add(new ValidationIssue(
                    Severity.Warning,
                    "keyframe-past-clip-end",
                    $"{clipPath}/{name}",
                    $"A keyframe on '{clip.Name}' sits at {Timecode.FormatClock(keyframed.End)}, past the clip's "
                    + $"{Timecode.FormatClock(clip.Duration)}; it will never be reached."));
            }

            if (keyframed.Start.IsNegative)
            {
                issues.Add(new ValidationIssue(
                    Severity.Warning,
                    "keyframe-before-clip-start",
                    $"{clipPath}/{name}",
                    $"A keyframe on '{clip.Name}' sits before the clip starts and will never be reached."));
            }
        }
    }

    private static IEnumerable<(string Name, AnimatedValue? Value)> EnumerateClipParameters(Clip clip)
    {
        yield return ("opacity", clip.Opacity);
        yield return ("volume", clip.Volume);
        yield return ("pan", clip.Pan);

        if (clip.Transform is { } transform)
        {
            yield return ("transform/position", transform.Position);
            yield return ("transform/scale", transform.Scale);
            yield return ("transform/rotation", transform.Rotation);
            yield return ("transform/anchor", transform.Anchor);
        }

        for (int index = 0; index < clip.Effects.Length; index++)
        {
            foreach (EffectParameter parameter in clip.Effects[index].Parameters)
            {
                yield return ($"effects/{index}/parameters/{parameter.Name}", parameter.Value);
            }
        }
    }

    private static void CheckTransitions(
        Track track,
        string trackPath,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        for (int index = 0; index < track.Transitions.Length; index++)
        {
            Transition transition = track.Transitions[index];
            string path = $"{trackPath}/transitions/{index}";

            Clip? left = track.Clip(transition.LeftClipId);
            Clip? right = track.Clip(transition.RightClipId);

            if (left is null || right is null)
            {
                issues.Add(new ValidationIssue(
                    Severity.Error,
                    "missing-transition-clip",
                    path,
                    "A transition references a clip that is not on this track."));
                continue;
            }

            if (left.End != right.Start)
            {
                issues.Add(new ValidationIssue(
                    Severity.Error,
                    "transition-not-at-cut",
                    path,
                    $"The transition between '{left.Name}' and '{right.Name}' is not at a cut: the first ends at "
                    + $"{Timecode.FormatClock(left.End)} and the second starts at {Timecode.FormatClock(right.Start)}."));
            }

            if (transition.Duration.Value <= 0)
            {
                issues.Add(new ValidationIssue(
                    Severity.Error,
                    "empty-transition",
                    $"{path}/duration",
                    "A transition with no duration does nothing."));
            }
            else if (transition.Duration > left.Duration || transition.Duration > right.Duration)
            {
                issues.Add(new ValidationIssue(
                    Severity.Warning,
                    "transition-longer-than-clip",
                    $"{path}/duration",
                    $"The transition between '{left.Name}' and '{right.Name}' is longer than one of them "
                    + "and will be clamped."));
            }
        }
    }

    /// <summary>
    /// Walks the nesting graph looking for a sequence that eventually contains itself. Nesting is
    /// unlimited in depth but a cycle would make rendering never terminate.
    /// </summary>
    private static void CheckSequenceCycles(Project project, ImmutableArray<ValidationIssue>.Builder issues)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var settled = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < project.Sequences.Length; index++)
        {
            Sequence sequence = project.Sequences[index];
            if (HasCycle(project, sequence, visiting, settled))
            {
                issues.Add(new ValidationIssue(
                    Severity.Error,
                    "sequence-cycle",
                    $"/sequences/{index}",
                    $"Sequence '{sequence.Name}' eventually contains itself, which can never be rendered."));
            }
        }
    }

    private static bool HasCycle(
        Project project,
        Sequence sequence,
        HashSet<string> visiting,
        HashSet<string> settled)
    {
        if (settled.Contains(sequence.Id))
        {
            return false;
        }

        if (!visiting.Add(sequence.Id))
        {
            return true;
        }

        foreach (Track track in sequence.Tracks)
        {
            foreach (Clip clip in track.Clips)
            {
                if (clip.SequenceId is { } nestedId &&
                    project.Sequence(nestedId) is { } nested &&
                    HasCycle(project, nested, visiting, settled))
                {
                    visiting.Remove(sequence.Id);
                    return true;
                }
            }
        }

        visiting.Remove(sequence.Id);
        settled.Add(sequence.Id);
        return false;
    }

    private static bool IsNoneOrNull(this Fade? fade) => fade is null || fade.IsNone;
}
