using System.Collections.Immutable;
using JazzHands.App.ViewModels.Timeline;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.App.Controls.Timeline;

/// <summary>
/// Overlapping cues on a subtitle track side by side: each cue in a lane of the track's row, the
/// lowest free when it starts, so no two that show at once are drawn over each other.
/// </summary>
/// <remarks>
/// Only a subtitle track can hold clips that overlap; any other track has one lane. Lane 0 is at
/// the top of the row and each takes an equal share of its height.
/// </remarks>
public sealed class CueLanes
{
    private readonly Dictionary<string, int> _lanes;

    private CueLanes(Dictionary<string, int> lanes, int count)
    {
        _lanes = lanes;
        Count = count;
    }

    /// <summary>One lane: a track whose clips never overlap.</summary>
    public static CueLanes One { get; } = new([], 1);

    /// <summary>How many lanes the row is shared between.</summary>
    public int Count { get; }

    /// <summary>The lanes of a track's clips.</summary>
    public static CueLanes Of(TrackKind kind, ImmutableArray<ClipView> clips)
    {
        if (kind != TrackKind.Subtitle || clips.Length < 2)
        {
            return One;
        }

        var lanes = new Dictionary<string, int>(StringComparer.Ordinal);
        var ends = new List<Flicks>();
        foreach (ClipView clip in clips.OrderBy(clip => clip.Start).ThenBy(clip => clip.End))
        {
            int lane = ends.FindIndex(end => end <= clip.Start);
            if (lane < 0)
            {
                lane = ends.Count;
                ends.Add(clip.End);
            }
            else
            {
                ends[lane] = clip.End;
            }

            lanes[clip.Clip.Id] = lane;
        }

        return ends.Count == 1 ? One : new CueLanes(lanes, ends.Count);
    }

    /// <summary>A clip's lane; 0 for one this does not know.</summary>
    public int LaneOf(string clipId) => _lanes.GetValueOrDefault(clipId);

    /// <summary>A clip's share of the space its row gives clips: its top and height.</summary>
    public (double Top, double Height) Slice(string clipId, double top, double height)
    {
        double each = height / Count;
        return (top + (LaneOf(clipId) * each), Math.Max(1.0, each));
    }

    /// <summary>The lane at a height in the space the row gives clips.</summary>
    public int LaneAt(double y, double top, double height) =>
        Count == 1 ? 0 : Math.Clamp((int)Math.Floor((y - top) / (height / Count)), 0, Count - 1);
}
