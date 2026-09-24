using System.Collections.Immutable;
using JazzHands.App.Controls.Timeline;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.App.ViewModels.Timeline;

/// <summary>What a time snapped to, most important first: a tie goes to the earlier kind.</summary>
public enum SnapKind
{
    /// <summary>The playhead.</summary>
    Playhead,

    /// <summary>The in or out point.</summary>
    InOut,

    /// <summary>A marker, or the end of a range marker.</summary>
    Marker,

    /// <summary>A clip's start or end.</summary>
    ClipEdge,

    /// <summary>The start of the sequence.</summary>
    Start,
}

/// <summary>A time something can snap to.</summary>
/// <param name="Time">Where.</param>
/// <param name="Kind">What is there.</param>
public readonly record struct SnapTarget(Flicks Time, SnapKind Kind);

/// <summary>
/// Where dragged edges snap: clip edges, markers, the playhead, the in and out points and zero,
/// within <see cref="Radius"/> pixels.
/// </summary>
/// <remarks>
/// Built once per gesture from the sequence as it was when the gesture began, leaving out the
/// clips being dragged, so a clip never snaps to where it already is. The radius is in pixels
/// because that is what the hand feels: at any zoom, eight pixels is the same flick of the wrist.
/// </remarks>
public sealed class SnapService
{
    /// <summary>How close, in pixels, an edge has to come to a target to snap to it.</summary>
    public const double Radius = 8.0;

    private readonly ImmutableArray<SnapTarget> _targets;

    private SnapService(ImmutableArray<SnapTarget> targets) => _targets = targets;

    /// <summary>A snap service with nothing to snap to, for when snapping is off.</summary>
    public static SnapService None { get; } = new([]);

    /// <summary>Every target, sorted by time.</summary>
    public ImmutableArray<SnapTarget> Targets => _targets;

    /// <summary>The targets in a sequence, leaving out some clips.</summary>
    /// <param name="sequence">The sequence.</param>
    /// <param name="playhead">Where the playhead is.</param>
    /// <param name="exclude">Clips whose edges are not targets: the ones being dragged.</param>
    public static SnapService For(Sequence sequence, Flicks playhead, IReadOnlyCollection<string>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        var targets = new List<SnapTarget>
        {
            new(Flicks.Zero, SnapKind.Start),
            new(playhead, SnapKind.Playhead),
        };

        if (sequence.InOut is { } inOut)
        {
            targets.Add(new SnapTarget(inOut.Start, SnapKind.InOut));
            targets.Add(new SnapTarget(inOut.End, SnapKind.InOut));
        }

        foreach (Marker marker in sequence.Markers)
        {
            targets.Add(new SnapTarget(marker.Time, SnapKind.Marker));
            if (marker.IsRange)
            {
                targets.Add(new SnapTarget(marker.Time + marker.Duration, SnapKind.Marker));
            }
        }

        foreach (Track track in sequence.Tracks)
        {
            foreach (Clip clip in track.Clips)
            {
                if (exclude?.Contains(clip.Id) == true)
                {
                    continue;
                }

                targets.Add(new SnapTarget(clip.Start, SnapKind.ClipEdge));
                targets.Add(new SnapTarget(clip.End, SnapKind.ClipEdge));
            }
        }

        return new SnapService([.. targets.OrderBy(target => target.Time).ThenBy(target => target.Kind)]);
    }

    /// <summary>The target nearest a time, within the radius at this zoom, or null.</summary>
    public SnapTarget? Find(Flicks time, TimelineGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        SnapTarget? best = null;
        double bestDistance = double.MaxValue;
        double x = geometry.XOf(time);

        foreach (SnapTarget target in _targets)
        {
            double distance = Math.Abs(geometry.XOf(target.Time) - x);
            if (distance > Radius)
            {
                continue;
            }

            // Nearer wins; at the same distance the more important kind does.
            if (distance < bestDistance || (distance == bestDistance && target.Kind < best!.Value.Kind))
            {
                best = target;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// Snaps a group that moves together: whichever of its edges is nearest a target decides, and
    /// the answer is how far to move them all to get it there.
    /// </summary>
    /// <param name="edges">Where the group's edges would be before snapping.</param>
    /// <param name="geometry">The zoom.</param>
    /// <returns>The correction and the target it lands on, or null when nothing is near.</returns>
    public (Flicks Correction, SnapTarget Target)? FindFor(IEnumerable<Flicks> edges, TimelineGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(geometry);

        (Flicks Correction, SnapTarget Target)? best = null;
        double bestDistance = double.MaxValue;

        foreach (Flicks edge in edges)
        {
            if (Find(edge, geometry) is not { } target)
            {
                continue;
            }

            double distance = Math.Abs(geometry.XOf(target.Time) - geometry.XOf(edge));
            if (distance < bestDistance)
            {
                best = (target.Time - edge, target);
                bestDistance = distance;
            }
        }

        return best;
    }
}
