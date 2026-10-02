using System.Collections.Immutable;
using JazzHands.Core.Model;
using JazzHands.Core.Queries;
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
            CheckSequence(project, project.Sequences[sequenceIndex], sequenceIndex, mediaIds, sequenceIds, issues);
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
        Project project,
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

        if (sequence.Master?.Ceiling is { } ceiling && !(ceiling is >= -24.0 and <= 0.0))
        {
            issues.Add(new ValidationIssue(
                Severity.Warning,
                "ceiling-out-of-range",
                $"{sequencePath}/master/ceiling",
                $"The master limiter's ceiling on '{sequence.Name}' is {ceiling} dBTP; it plays as the nearest of -24 to 0."));
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
            CheckTransitions(project, sequence, track, trackPath, issues);
        }

        if (sequence.Multicam is { } multicam)
        {
            CheckMulticam(sequence, multicam, $"{sequencePath}/multicam", trackIds, issues);
        }
    }

    /// <summary>
    /// A colour graph (Phase 44) reads nodes it has, never goes round in a circle, mixes two to
    /// four pictures, is keyed only by qualifiers, and holds only colour corrections. Each is a
    /// warning: a graph the renderer cannot follow shows its picture unchanged, and a node of a
    /// type it cannot draw passes its picture on.
    /// </summary>
    private static void CheckGraph(Effect effect, GradeGraph graph, string path, ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (effect.TypeId != GradeGraph.TypeId)
        {
            issues.Add(new ValidationIssue(Severity.Warning, "graph-on-other-effect", path, $"Effect '{effect.Id}' is a '{effect.TypeId}', which does not use nodes; they are ignored."));
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < graph.Nodes.Length; index++)
        {
            GradeNode node = graph.Nodes[index];
            string nodePath = $"{path}/nodes/{index}";
            if (!ids.Add(node.Id))
            {
                issues.Add(new ValidationIssue(Severity.Error, "duplicate-id", $"{nodePath}/effect/id", $"Two nodes of colour graph '{effect.Id}' have the id '{node.Id}'."));
            }

            if (!node.IsMix && !GradeGraph.NodeTypes.Contains(node.Effect.TypeId))
            {
                issues.Add(new ValidationIssue(Severity.Warning, "unknown-node-type", $"{nodePath}/effect/typeId", $"Node '{node.Id}' is a '{node.Effect.TypeId}'; a node is a colour correction or a mix, so it passes its picture on."));
            }

            if (node.IsMix && node.Inputs.Length is < 1 or > GradeGraph.MostInputs)
            {
                issues.Add(new ValidationIssue(Severity.Warning, "mix-inputs", $"{nodePath}/inputs", $"Mix '{node.Id}' has {node.Inputs.Length} inputs; a mix takes one to {GradeGraph.MostInputs}."));
            }
            else if (!node.IsMix && node.Inputs.Length > 1)
            {
                issues.Add(new ValidationIssue(Severity.Warning, "node-inputs", $"{nodePath}/inputs", $"Node '{node.Id}' has {node.Inputs.Length} inputs; only a mix reads more than one, so it reads the first."));
            }

            foreach (string input in node.Inputs)
            {
                if (graph.Node(input) is null)
                {
                    issues.Add(new ValidationIssue(Severity.Warning, "missing-node", $"{nodePath}/inputs", $"Node '{node.Id}' reads '{input}', which the graph does not have."));
                }
            }

            if (node.Key is { } key && graph.Node(key)?.Effect.TypeId != "color.hsl")
            {
                issues.Add(new ValidationIssue(Severity.Warning, "key-not-qualifier", $"{nodePath}/key", $"Node '{node.Id}' is keyed by '{key}', which is not an HSL qualifier node of the graph; it applies everywhere."));
            }

            if (node.Effect.Graph is not null)
            {
                issues.Add(new ValidationIssue(Severity.Warning, "nested-graph", $"{nodePath}/effect/graph", $"Node '{node.Id}' holds nodes of its own; a graph does not go inside a graph, so they are ignored."));
            }
        }

        if (graph.Output is { } output && graph.Node(output) is null)
        {
            issues.Add(new ValidationIssue(Severity.Warning, "missing-node", $"{path}/output", $"Colour graph '{effect.Id}' shows node '{output}', which it does not have; it shows its picture unchanged."));
        }
        else if (graph.Order() is null)
        {
            issues.Add(new ValidationIssue(Severity.Warning, "graph-cycle", path, $"Colour graph '{effect.Id}' goes round in a circle or reads a node it does not have, so it shows its picture unchanged."));
        }
    }
    /// <summary>
    /// A multicam's angles name tracks of its sequence, and its switches name angles it has and
    /// come in time order (Phase 41). Each is a warning: the picture plays the nearest angle and
    /// a missing track is simply not shown.
    /// </summary>
    private static void CheckMulticam(Sequence sequence, Multicam multicam, string path, HashSet<string> trackIds, ImmutableArray<ValidationIssue>.Builder issues)
    {
        for (int angle = 0; angle < multicam.Angles.Length; angle++)
        {
            MulticamAngle each = multicam.Angles[angle];
            foreach (string id in (each.PictureTrackId is { } picture ? [picture] : Array.Empty<string>()).Concat(each.SoundTrackIds))
            {
                if (!trackIds.Contains(id))
                {
                    issues.Add(new ValidationIssue(
                        Severity.Warning,
                        "missing-angle-track",
                        $"{path}/angles/{angle}",
                        $"Angle {angle + 1} of '{sequence.Name}' names track '{id}', which the sequence does not have."));
                }
            }
        }

        for (int index = 0; index < multicam.Switches.Length; index++)
        {
            AngleSwitch cut = multicam.Switches[index];
            if (cut.Picture is < 0 || cut.Picture >= multicam.Angles.Length || cut.Sound is < 0 || cut.Sound >= multicam.Angles.Length)
            {
                issues.Add(new ValidationIssue(
                    Severity.Warning,
                    "angle-out-of-range",
                    $"{path}/switches/{index}",
                    $"A switch in '{sequence.Name}' names an angle it does not have; it plays the nearest."));
            }

            if (index > 0 && cut.At <= multicam.Switches[index - 1].At)
            {
                issues.Add(new ValidationIssue(
                    Severity.Warning,
                    "switches-out-of-order",
                    $"{path}/switches/{index}",
                    $"The switches in '{sequence.Name}' are not in time order; they are read in the order they are written."));
            }
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

            bool subtitles = track.Kind == TrackKind.Subtitle;
            if (subtitles != clip.Cue is not null)
            {
                issues.Add(new ValidationIssue(
                    Severity.Warning,
                    subtitles ? "not-a-cue" : "cue-off-subtitle-track",
                    clipPath,
                    subtitles
                        ? $"'{clip.Name}' is on subtitle track '{track.Name}' but says nothing; it is neither shown nor exported."
                        : $"'{clip.Name}' is a subtitle cue on {track.Kind.ToString().ToLowerInvariant()} track '{track.Name}'; cues show only on a subtitle track."));
            }

            // Cues may overlap: two people talking at once stack on the frame.
            if (clip.Start < previousEnd && !subtitles)
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

            if (effect.Graph is { } graph)
            {
                CheckGraph(effect, graph, $"{effectPath}/graph", issues);
            }
        }

        for (int trackIndex = 0; trackIndex < clip.PointTracks.Length; trackIndex++)
        {
            PointTrack track = clip.PointTracks[trackIndex];
            string trackPath = $"{clipPath}/pointTracks/{trackIndex}";
            if (!Id.IsValid(track.Id))
            {
                issues.Add(new ValidationIssue(Severity.Error, "invalid-id", $"{trackPath}/id", $"'{track.Id}' is not a ULID."));
            }

            for (int point = 1; point < track.Points.Length; point++)
            {
                if (track.Points[point].Time <= track.Points[point - 1].Time)
                {
                    issues.Add(new ValidationIssue(
                        Severity.Error,
                        "points-out-of-order",
                        $"{trackPath}/points/{point}",
                        $"The points of '{track.Name}' on '{clip.Name}' must go forwards in time, each after the one before."));
                    break;
                }
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
            if (value is DrivenValue driven)
            {
                try
                {
                    Drivers.DriverExpression.Parse(driven.Expression);
                }
                catch (Drivers.DriverSyntaxException error)
                {
                    issues.Add(new ValidationIssue(
                        Severity.Warning,
                        "invalid-driver",
                        $"{clipPath}/{name}",
                        $"The driver on '{clip.Name}' does not read: {error.Message}. The value underneath it is used."));
                }

                continue;
            }

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

        if (clip.Layer3D is { } space)
        {
            yield return ("layer3D/z", space.Z);
            yield return ("layer3D/rotationX", space.RotationX);
            yield return ("layer3D/rotationY", space.RotationY);
            yield return ("layer3D/ambient", space.Ambient);
            yield return ("layer3D/diffuse", space.Diffuse);
            yield return ("layer3D/specular", space.Specular);
            yield return ("layer3D/roughness", space.Roughness);
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
        Project project,
        Sequence sequence,
        Track track,
        string trackPath,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (track.Transitions.IsEmpty)
        {
            return;
        }

        Rational frameRate = project.SettingsFor(sequence).FrameRate;
        var joined = new HashSet<(string Left, string Right)>();

        if (track.Kind is not (TrackKind.Video or TrackKind.Audio))
        {
            issues.Add(new ValidationIssue(
                Severity.Warning,
                "transition-wrong-track",
                $"{trackPath}/transitions",
                $"'{track.Name}' is not a picture or a sound track, so its transitions do nothing."));
        }

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
            else if (TransitionTiming.Span(track, transition, frameRate) is { } span)
            {
                if (span.IsClamped)
                {
                    issues.Add(new ValidationIssue(
                        Severity.Warning,
                        "transition-longer-than-clip",
                        $"{path}/duration",
                        $"The transition between '{left.Name}' and '{right.Name}' asks for {Timecode.FormatClock(transition.Duration)} "
                        + $"but the clips leave room for {Timecode.FormatClock(span.Range.Duration)}, so it plays that long."));
                }

                (Flicks leftShort, Flicks rightShort) = TransitionTiming.Shortfall(project, span);
                if (leftShort.Value > 0 || rightShort.Value > 0)
                {
                    issues.Add(new ValidationIssue(
                        Severity.Warning,
                        "insufficient-handles",
                        path,
                        ShortHandles(left, right, leftShort, rightShort)));
                }
            }

            if (!joined.Add((transition.LeftClipId, transition.RightClipId)))
            {
                issues.Add(new ValidationIssue(
                    Severity.Error,
                    "duplicate-transition",
                    path,
                    $"There are two transitions between '{left.Name}' and '{right.Name}'. A cut takes one."));
            }
        }
    }

    /// <summary>What an insufficient-handles warning says: which clip is short, by how much, and what happens.</summary>
    public static string ShortHandles(Clip left, Clip right, Flicks leftShort, Flicks rightShort)
    {
        var parts = new List<string>(2);
        if (leftShort.Value > 0)
        {
            parts.Add($"'{left.Name}' needs {Timecode.FormatClock(leftShort)} more source after its end");
        }

        if (rightShort.Value > 0)
        {
            parts.Add($"'{right.Name}' needs {Timecode.FormatClock(rightShort)} more source before its start");
        }

        return $"{string.Join(" and ", parts)} than the file has, so the transition holds a frame there. "
            + "Shorten the transition, or add it again with --handles trim to trim the clips back.";
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
