using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Editing;

public static partial class EditOps
{
    /// <summary>
    /// Cuts a clip at a timeline time and, with <paramref name="linked"/>, every clip linked to it
    /// that runs across the same moment on an unlocked track: a picture and its sound are cut
    /// together, as editors do.
    /// </summary>
    /// <remarks>
    /// The left pieces keep their ids and their link; the right pieces are linked to each other in
    /// a group of their own, so each half moves with its own sound and not with the other half's.
    /// When nothing linked runs across the moment the right piece keeps the link as it was, as
    /// does a clip cut alone: what it is linked to then lies on one side or runs across both.
    /// </remarks>
    /// <param name="sequence">The sequence.</param>
    /// <param name="clipId">The clip to cut.</param>
    /// <param name="at">Where to cut, on the timeline.</param>
    /// <param name="rightId">The id for the clip's right piece.</param>
    /// <param name="newId">Makes the ids of the linked clips' right pieces, and the right pieces' link.</param>
    /// <param name="linked">Cut the clips linked to it too.</param>
    public static EditResult<Sequence> SplitLinked(Sequence sequence, string clipId, Flicks at, string rightId, Func<string> newId, bool linked = true)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(newId);

        if (sequence.Tracks.FirstOrDefault(track => track.Clip(clipId) is not null) is not { } home)
        {
            return EditError.ClipNotFound(clipId);
        }

        Clip clip = home.Clip(clipId)!;
        EditResult<Track> first = Split(home, clipId, at, rightId);
        if (!first.IsOk)
        {
            return first.Error!;
        }

        Sequence result = sequence.ReplaceTrack(first.Value);
        if (!linked || clip.LinkGroupId is not { } group)
        {
            return result;
        }

        var rights = new List<(string Track, string Clip)> { (home.Id, rightId) };
        foreach (Track track in sequence.Tracks.Where(track => !track.Locked))
        {
            foreach (Clip partner in track.Clips.Where(other => other.Id != clipId && other.LinkGroupId == group && other.Start < at && at < other.End))
            {
                string id = newId();
                result = result.ReplaceTrack(Split(result.Track(track.Id)!, partner.Id, at, id).Value);
                rights.Add((track.Id, id));
            }
        }

        if (rights.Count == 1)
        {
            return result;
        }

        string pair = newId();
        foreach ((string trackId, string id) in rights)
        {
            Track track = result.Track(trackId)!;
            result = result.ReplaceTrack(track.ReplaceClip(track.Clip(id)! with { LinkGroupId = pair }));
        }

        return result;
    }

    /// <summary>
    /// The clips to send a split to, so each is cut once: a clip linked to one earlier in the list
    /// is left out, since that one's split cuts it. Ids the sequence does not have are kept.
    /// </summary>
    public static IReadOnlyList<string> OnePerLink(Sequence sequence, IEnumerable<string> clipIds)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(clipIds);

        var links = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();
        foreach (string id in clipIds)
        {
            string? link = sequence.Tracks.Select(track => track.Clip(id)).FirstOrDefault(clip => clip is not null)?.LinkGroupId;
            if (link is null || links.Add(link))
            {
                kept.Add(id);
            }
        }

        return kept;
    }
}
