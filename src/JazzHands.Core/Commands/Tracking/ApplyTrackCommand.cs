namespace JazzHands.Core.Commands;

/// <summary>Makes something follow a tracked point: a clip's position, an effect's point, or a mask.</summary>
/// <remarks>
/// Writes a keyframe for every tracked frame. A clip follows with <c>transform.position</c>, an
/// effect with its point parameter (a callout's <c>target</c>, or the one named with
/// <c>--param</c>), both in sequence pixels, where the tracked clip puts the point on the frame. By
/// default what follows keeps its distance from the point, moving as it moves from the first
/// tracked frame; <c>--absolute</c> puts it on the point. A mask on the tracked clip moves its
/// shape with the point, in the clip's own pixels. One undo.
/// </remarks>
/// <param name="TrackId">The point track.</param>
/// <param name="To">What follows it: a clip, an effect or a mask.</param>
/// <param name="Param">Which parameter, when it is not the usual one.</param>
/// <param name="Absolute">Put it on the point, rather than keeping its distance.</param>
[Command("tracking.apply", Description = "Make a clip, an effect's point or a mask follow a tracked point")]
public sealed record ApplyTrackCommand(
    [property: Arg(0, "The point track id")] string TrackId,
    [property: Option("to", "The clip, effect or mask that follows")] string To,
    [property: Option("param", "Which parameter follows, when not the usual one")] string? Param = null,
    [property: Option("absolute", "Put it on the point rather than keeping its distance")] bool Absolute = false) : ICommand;

/// <summary>Removes a point track from its clip. What already follows it keeps its keyframes.</summary>
/// <param name="TrackId">The point track.</param>
[Command("tracking.remove", Description = "Remove a point track")]
public sealed record RemovePointTrackCommand(
    [property: Arg(0, "The point track id")] string TrackId) : ICommand;
