using System.Collections.Immutable;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Editing;

/// <summary>One copied clip: the clip, where it sat relative to the others, and on which track.</summary>
/// <param name="Clip">The clip as it was.</param>
/// <param name="Kind">The kind of track it was on.</param>
/// <param name="TrackNumber">
/// Its track's place among the tracks of its family, from zero: V1 is 0 and V2 is 1 for pictures,
/// A1 is 0 for sound.
/// </param>
/// <param name="Offset">How far after the earliest copied clip it starts.</param>
public sealed record ClipboardClip(Clip Clip, TrackKind Kind, int TrackNumber, Flicks Offset) : IEquatable<ClipboardClip>;

/// <summary>
/// What a copy puts on the clipboard: the clips and the media they play, with full paths.
/// </summary>
/// <remarks>
/// Media travel with their clips so that pasting into another project brings them along; paths
/// are made full at copy time because the other project lives somewhere else. Serialized as JSON
/// for the Windows clipboard and for <c>clip.paste --data</c>.
/// </remarks>
/// <param name="Clips">The clips, earliest first.</param>
/// <param name="Media">The media they play, paths made full.</param>
/// <param name="Version">The shape of this record, so an older build can refuse a newer one.</param>
public sealed record ClipboardContent(
    EquatableArray<ClipboardClip> Clips,
    EquatableArray<MediaItem> Media = default,
    int Version = ClipboardOps.CurrentVersion) : IEquatable<ClipboardContent>;

/// <summary>Copying clips out of a sequence and pasting them into one, in the same project or another.</summary>
public static class ClipboardOps
{
    /// <summary>The clipboard shape this build writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Copies clips, with the media they play.</summary>
    /// <param name="project">The project.</param>
    /// <param name="clipIds">The clips, all in one sequence.</param>
    /// <param name="fullPath">Turns a media item's stored path into a full one.</param>
    public static EditResult<ClipboardContent> Copy(Project project, IReadOnlyList<string> clipIds, Func<MediaItem, string> fullPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(clipIds);
        ArgumentNullException.ThrowIfNull(fullPath);

        if (clipIds.Count == 0)
        {
            return EditError.NothingSelected("Copy needs a clip.");
        }

        var found = new List<(Sequence Sequence, Track Track, Clip Clip)>();
        foreach (string clipId in clipIds.Distinct(StringComparer.Ordinal))
        {
            if (project.FindClip(clipId) is not { } location)
            {
                return EditError.ClipNotFound(clipId);
            }

            found.Add((location.Sequence, location.Track, location.Clip));
        }

        if (found.Select(entry => entry.Sequence.Id).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            return EditError.NotAligned("Copy takes clips from one sequence at a time.");
        }

        Sequence sequence = found[0].Sequence;
        Flicks earliest = found.Min(entry => entry.Clip.Start);

        ClipboardClip[] clips =
        [
            .. found
                .OrderBy(entry => entry.Clip.Start)
                .Select(entry => new ClipboardClip(
                    entry.Clip,
                    entry.Track.Kind,
                    Number(sequence, entry.Track),
                    entry.Clip.Start - earliest)),
        ];

        MediaItem[] media =
        [
            .. clips
                .Select(clip => clip.Clip.MediaId)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Select(project.MediaItem)
                .OfType<MediaItem>()
                .Select(item => item with { RelativePath = fullPath(item) }),
        ];

        return new ClipboardContent(new EquatableArray<ClipboardClip>(clips), new EquatableArray<MediaItem>(media));
    }

    /// <summary>
    /// Pastes copied clips into a sequence at a time, over what is there or pushing it on.
    /// </summary>
    /// <remarks>
    /// Each clip goes on the track with the same number in its family as the one it came from, or,
    /// when a target track is given, its family moves so the lowest copied track lands on the
    /// target. Media already in the project, by content hash or by path, is reused; the rest is
    /// added. Every clip gets a new id, and clips that were linked or grouped together are linked
    /// and grouped together again, under new group ids.
    /// </remarks>
    /// <param name="project">The project.</param>
    /// <param name="sequenceId">The sequence to paste into.</param>
    /// <param name="content">What was copied.</param>
    /// <param name="at">Where the earliest clip goes.</param>
    /// <param name="targetTrackId">The track the lowest copied clip of that family goes on, or null for the same numbers.</param>
    /// <param name="insert">Push what is there on instead of pasting over it.</param>
    /// <param name="fullPath">Turns a media item's stored path into a full one, to match media by path.</param>
    /// <param name="store">Turns a full path into what the project stores.</param>
    /// <param name="newId">Makes identifiers.</param>
    public static EditResult<Project> Paste(
        Project project,
        string sequenceId,
        ClipboardContent content,
        Flicks at,
        string? targetTrackId,
        bool insert,
        Func<MediaItem, string> fullPath,
        Func<string, string> store,
        Func<string> newId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(newId);

        if (content.Version > CurrentVersion)
        {
            return new EditError("clipboard-too-new", "Those clips were copied by a newer Jazz Hands.");
        }

        if (content.Clips.IsEmpty)
        {
            return EditError.NothingSelected("The clipboard has no clips.");
        }

        if (project.Sequence(sequenceId) is not { } sequence)
        {
            return new EditError("sequence-not-found", $"No sequence with id '{sequenceId}'.");
        }

        Track? target = targetTrackId is null ? null : sequence.Track(targetTrackId);
        if (targetTrackId is not null && target is null)
        {
            return EditError.TrackNotFound(targetTrackId);
        }

        // The media: reused when the project already has it, added when it does not.
        Project result = project;
        var mediaIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (MediaItem copied in content.Media)
        {
            MediaItem? existing = project.Media.FirstOrDefault(item =>
                (copied.Hash.Length > 0 && string.Equals(item.Hash, copied.Hash, StringComparison.Ordinal))
                || string.Equals(fullPath(item), copied.RelativePath, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                mediaIds[copied.Id] = existing.Id;
                continue;
            }

            MediaItem added = copied with { Id = newId(), RelativePath = store(copied.RelativePath) };
            mediaIds[copied.Id] = added.Id;
            result = result.WithMedia(added);
        }

        // Which track each clip goes on.
        var placements = new List<Placement>(content.Clips.Length);
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (ClipboardClip copied in content.Clips)
        {
            List<Track> family = Family(sequence, copied.Kind);
            int number = copied.TrackNumber;

            if (target is not null && SameFamily(target.Kind, copied.Kind))
            {
                int lowest = content.Clips.Where(other => SameFamily(other.Kind, copied.Kind)).Min(other => other.TrackNumber);
                number += family.FindIndex(track => string.Equals(track.Id, target.Id, StringComparison.Ordinal)) - lowest;
            }

            if (number >= family.Count)
            {
                string prefix = copied.Kind == TrackKind.Audio ? "A" : "V";
                return new EditError(
                    "no-such-track",
                    $"'{copied.Clip.Name}' goes on {prefix}{number + 1}, and '{sequence.Name}' has no such track. Add one, or paste onto a lower track.");
            }

            if (copied.Clip.SequenceId is { } nested && result.Sequence(nested) is null)
            {
                return new EditError(
                    "missing-sequence-reference",
                    $"'{copied.Clip.Name}' plays a sequence this project does not have. Compound clips paste within their own project.");
            }

            Clip clip = copied.Clip with
            {
                Id = newId(),
                MediaId = copied.Clip.MediaId is { } mediaId ? mediaIds.GetValueOrDefault(mediaId, mediaId) : null,
                LinkGroupId = Regroup(copied.Clip.LinkGroupId, links, newId),
                GroupId = Regroup(copied.Clip.GroupId, groups, newId),
            };

            placements.Add(new Placement(family[number].Id, clip, copied.Offset));
        }

        EditResult<Sequence> pasted = insert
            ? EditOps.Insert(sequence, at, placements)
            : EditOps.Overwrite(sequence, at, placements);

        return pasted.IsOk ? result.ReplaceSequence(pasted.Value) : pasted.Error!;
    }

    private static string? Regroup(string? group, Dictionary<string, string> map, Func<string> newId)
    {
        if (group is null)
        {
            return null;
        }

        if (!map.TryGetValue(group, out string? renamed))
        {
            renamed = newId();
            map[group] = renamed;
        }

        return renamed;
    }

    private static bool SameFamily(TrackKind a, TrackKind b) =>
        (a == TrackKind.Audio) == (b == TrackKind.Audio);

    /// <summary>The tracks of a family in the order they are numbered.</summary>
    private static List<Track> Family(Sequence sequence, TrackKind kind) =>
        [.. sequence.Tracks.Where(track => SameFamily(track.Kind, kind)).OrderBy(track => track.Order)];

    private static int Number(Sequence sequence, Track track) =>
        Family(sequence, track.Kind).FindIndex(candidate => string.Equals(candidate.Id, track.Id, StringComparison.Ordinal));
}
