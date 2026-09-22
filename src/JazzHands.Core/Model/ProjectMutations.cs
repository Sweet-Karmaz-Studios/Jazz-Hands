namespace JazzHands.Core.Model;

/// <summary>Where a clip was found: which sequence, which track, and the clip itself.</summary>
/// <param name="Sequence">The sequence holding it.</param>
/// <param name="Track">The track holding it.</param>
/// <param name="Clip">The clip.</param>
public sealed record ClipLocation(Sequence Sequence, Track Track, Clip Clip);

/// <summary>
/// The only sanctioned way to change a <see cref="Project"/>.
/// </summary>
/// <remarks>
/// Every one of these returns a new project and leaves the old one untouched, because the undo
/// stack holds the old roots. Nothing here validates: these are the mechanics, and
/// <see cref="Validation.Validator"/> and the command handlers decide what is allowed. Keeping
/// those apart is what lets a hand-edited file be loaded, reported on, and repaired rather than
/// rejected.
/// </remarks>
public static class ProjectMutations
{
    /// <summary>Finds a clip anywhere in the project, with the track and sequence holding it.</summary>
    public static ClipLocation? FindClip(this Project project, string clipId)
    {
        ArgumentNullException.ThrowIfNull(project);

        foreach (Sequence sequence in project.Sequences)
        {
            foreach (Track track in sequence.Tracks)
            {
                Clip? clip = track.Clip(clipId);
                if (clip is not null)
                {
                    return new ClipLocation(sequence, track, clip);
                }
            }
        }

        return null;
    }

    /// <summary>The track holding a clip, or null.</summary>
    public static Track? TrackOf(this Project project, string clipId) => project.FindClip(clipId)?.Track;

    /// <summary>The track holding a clip within one sequence, or null.</summary>
    public static Track? TrackOf(this Sequence sequence, string clipId)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        foreach (Track track in sequence.Tracks)
        {
            if (track.Clip(clipId) is not null)
            {
                return track;
            }
        }

        return null;
    }

    /// <summary>
    /// Inserts a clip, keeping the track sorted by start time. Overlaps are allowed here and
    /// caught by validation; the editing operations are what refuse them.
    /// </summary>
    public static Track AddClip(this Track track, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);

        int index = 0;
        while (index < track.Clips.Length && track.Clips[index].Start <= clip.Start)
        {
            index++;
        }

        return track with { Clips = track.Clips.Insert(index, clip) };
    }

    /// <summary>Removes a clip by identifier. Returns the track unchanged when it is not there.</summary>
    public static Track RemoveClip(this Track track, string clipId)
    {
        ArgumentNullException.ThrowIfNull(track);

        int index = track.IndexOf(clipId);
        return index < 0 ? track : track with { Clips = track.Clips.RemoveAt(index) };
    }

    /// <summary>
    /// Replaces a clip in place, re-sorting when its start moved. Returns the track unchanged
    /// when the clip is not there.
    /// </summary>
    public static Track ReplaceClip(this Track track, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);

        int index = track.IndexOf(clip.Id);
        if (index < 0)
        {
            return track;
        }

        bool stillSorted =
            (index == 0 || track.Clips[index - 1].Start <= clip.Start) &&
            (index == track.Clips.Length - 1 || clip.Start <= track.Clips[index + 1].Start);

        return stillSorted
            ? track with { Clips = track.Clips.SetItem(index, clip) }
            : track.RemoveClip(clip.Id).AddClip(clip);
    }

    /// <summary>Replaces several clips at once, which is what most editing operations produce.</summary>
    public static Track ReplaceClips(this Track track, IEnumerable<Clip> clips)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clips);

        Track updated = track;
        foreach (Clip clip in clips)
        {
            updated = updated.ReplaceClip(clip);
        }

        return updated;
    }

    /// <summary>Replaces a track inside a sequence, keeping tracks sorted by order.</summary>
    public static Sequence ReplaceTrack(this Sequence sequence, Track track)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(track);

        int index = sequence.Tracks.IndexOf(candidate => string.Equals(candidate.Id, track.Id, StringComparison.Ordinal));
        return index < 0 ? sequence : sequence with { Tracks = sequence.Tracks.SetItem(index, track) };
    }

    /// <summary>Adds a track, keeping tracks sorted by order.</summary>
    public static Sequence AddTrack(this Sequence sequence, Track track)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(track);

        int index = 0;
        while (index < sequence.Tracks.Length && sequence.Tracks[index].Order <= track.Order)
        {
            index++;
        }

        return sequence with { Tracks = sequence.Tracks.Insert(index, track) };
    }

    /// <summary>Removes a track by identifier.</summary>
    public static Sequence RemoveTrack(this Sequence sequence, string trackId)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        int index = sequence.Tracks.IndexOf(track => string.Equals(track.Id, trackId, StringComparison.Ordinal));
        return index < 0 ? sequence : sequence with { Tracks = sequence.Tracks.RemoveAt(index) };
    }

    /// <summary>Replaces a sequence inside the project.</summary>
    public static Project ReplaceSequence(this Project project, Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);

        int index = project.Sequences.IndexOf(
            candidate => string.Equals(candidate.Id, sequence.Id, StringComparison.Ordinal));

        return index < 0 ? project : project with { Sequences = project.Sequences.SetItem(index, sequence) };
    }

    /// <summary>Adds a sequence to the project.</summary>
    public static Project AddSequence(this Project project, Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);

        return project with { Sequences = project.Sequences.Add(sequence) };
    }

    /// <summary>Replaces a track anywhere in the project, finding its sequence.</summary>
    public static Project ReplaceTrack(this Project project, Track track)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(track);

        foreach (Sequence sequence in project.Sequences)
        {
            if (sequence.Track(track.Id) is not null)
            {
                return project.ReplaceSequence(sequence.ReplaceTrack(track));
            }
        }

        return project;
    }

    /// <summary>Adds a media item, or replaces one with the same identifier.</summary>
    public static Project WithMedia(this Project project, MediaItem media)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(media);

        int index = project.Media.IndexOf(item => string.Equals(item.Id, media.Id, StringComparison.Ordinal));
        return index < 0
            ? project with { Media = project.Media.Add(media) }
            : project with { Media = project.Media.SetItem(index, media) };
    }

    /// <summary>Removes a media item by identifier. Clips referencing it are left alone for validation to report.</summary>
    public static Project RemoveMedia(this Project project, string mediaId)
    {
        ArgumentNullException.ThrowIfNull(project);

        int index = project.Media.IndexOf(item => string.Equals(item.Id, mediaId, StringComparison.Ordinal));
        return index < 0 ? project : project with { Media = project.Media.RemoveAt(index) };
    }

    /// <summary>The order value a new track should take to sit on top of the existing ones.</summary>
    public static int NextTrackOrder(this Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        int highest = -1;
        foreach (Track track in sequence.Tracks)
        {
            highest = Math.Max(highest, track.Order);
        }

        return highest + 1;
    }

    /// <summary>Stamps the modified time, which every mutating command does on its way out.</summary>
    public static Project Touch(this Project project, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project with { Modified = (clock ?? TimeProvider.System).GetUtcNow() };
    }
}
