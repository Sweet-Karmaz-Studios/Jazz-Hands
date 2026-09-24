using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Engine.Effects;

/// <summary>
/// Follows a mask's shape through a clip's picture and keyframes it to match. Planned for v1.1;
/// nothing implements it yet, and no command calls it.
/// </summary>
/// <remarks>
/// The shape of the work, fixed now so the model does not have to change later: a tracker reads
/// the clip's source frames across a range, starting from the mask as it is at
/// <see cref="MaskTrackRequest.From"/>, and returns keyframes for the mask's bounds or path at
/// every frame it tracked. Applying them is an ordinary undoable edit (a batch of
/// <c>keyframe.add</c> on the mask's <c>bounds</c> or <c>path</c> parameter), so a tracked mask is
/// just an animated one, and hand-edited keyframes after tracking are fine. The engine thread runs
/// it; the UI reports progress and can cancel. Expect a <c>mask.track</c> command over this.
/// </remarks>
public interface IMaskTracker
{
    /// <summary>A short name for the method, for the settings and the log ("points", "planar").</summary>
    string Name { get; }

    /// <summary>Tracks a mask across a range of its clip and returns the keyframes it found.</summary>
    /// <param name="project">The project the mask is in; an immutable snapshot.</param>
    /// <param name="request">Which mask, and over what.</param>
    /// <param name="progress">Fraction done, 0 to 1.</param>
    /// <param name="cancellationToken">Stops the tracking; what was found so far is discarded.</param>
    Task<MaskTrackResult> TrackAsync(Project project, MaskTrackRequest request, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>What to track.</summary>
/// <param name="MaskId">The mask, on a clip or on one of its picture effects.</param>
/// <param name="From">Where to start, clip-relative; the mask's shape there is the reference.</param>
/// <param name="To">Where to stop, clip-relative; before <paramref name="From"/> to track backwards.</param>
public sealed record MaskTrackRequest(string MaskId, Flicks From, Flicks To);

/// <summary>What a tracker found.</summary>
/// <param name="Parameter">The mask parameter the keyframes are for: <c>bounds</c> or <c>path</c>.</param>
/// <param name="Keyframes">One per tracked frame, clip-relative, in time order.</param>
/// <param name="LostAt">Where the tracker lost the shape, when it did; the keyframes stop there.</param>
public sealed record MaskTrackResult(string Parameter, EquatableArray<Keyframe> Keyframes, Flicks? LostAt = null);
