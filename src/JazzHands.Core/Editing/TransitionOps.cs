using System.Collections.Immutable;
using JazzHands.Core.Model;

namespace JazzHands.Core.Editing;

/// <summary>Keeps transitions true to the clips they join as the clips are edited.</summary>
/// <remarks>
/// A transition names its two clips and lives only while they meet at a cut. Rather than every
/// edit knowing about transitions, the dispatcher settles each sequence an edit touched: a
/// transition whose clips were removed, moved apart or put on other tracks goes, in the same undo
/// step as the edit that separated them. A ripple, a roll and a slide keep the clips together and
/// the transition with them; a split keeps it on the piece that is still at the cut.
/// </remarks>
public static class TransitionOps
{
    /// <summary>
    /// A sequence without the transitions whose clips no longer meet on their track, and the
    /// identifiers of those removed. The same sequence when every one still stands.
    /// </summary>
    public static (Sequence Sequence, ImmutableArray<string> Removed) Settle(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        Sequence result = sequence;
        var removed = ImmutableArray.CreateBuilder<string>();

        foreach (Track track in sequence.Tracks)
        {
            if (track.Transitions.IsEmpty)
            {
                continue;
            }

            var kept = ImmutableArray.CreateBuilder<Transition>(track.Transitions.Length);
            foreach (Transition transition in track.Transitions)
            {
                if (Meets(track, transition))
                {
                    kept.Add(transition);
                }
                else
                {
                    removed.Add(transition.Id);
                }
            }

            if (kept.Count != track.Transitions.Length)
            {
                result = result.ReplaceTrack(track with { Transitions = new EquatableArray<Transition>(kept.ToImmutable()) });
            }
        }

        return (result, removed.ToImmutable());
    }

    /// <summary>True when a transition's clips are both on its track and the first ends where the second starts.</summary>
    public static bool Meets(Track track, Transition transition)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(transition);

        return track.Clip(transition.LeftClipId) is { } left
            && track.Clip(transition.RightClipId) is { } right
            && left.End == right.Start;
    }

    /// <summary>
    /// A track after one of its clips was split in two: the transition at the clip's end moves to
    /// the right piece, which is the one still at that cut. The one at its start stays with the
    /// left piece, which keeps the clip's identifier.
    /// </summary>
    public static Track AfterSplit(Track track, string clipId, string rightPieceId)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (track.Transitions.IsEmpty)
        {
            return track;
        }

        int index = track.Transitions.IndexOf(transition => string.Equals(transition.LeftClipId, clipId, StringComparison.Ordinal));
        return index < 0
            ? track
            : track with { Transitions = track.Transitions.SetItem(index, track.Transitions[index] with { LeftClipId = rightPieceId }) };
    }
}
