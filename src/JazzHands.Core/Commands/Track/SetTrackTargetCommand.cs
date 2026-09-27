namespace JazzHands.Core.Commands;

/// <summary>Targets a track for edits from the source monitor, or stops targeting it.</summary>
/// <remarks>
/// The first targeted video track takes the source's picture and the targeted audio tracks, lowest
/// first, take its sound streams in order; an untargeted track gets nothing. Targeting a video
/// track untargets the others. Kept in the sequence, so it is saved and undoable.
/// </remarks>
/// <param name="TrackId">The track.</param>
/// <param name="On">True to target it.</param>
[Command("track.set-target", Description = "Target a track for edits from the source monitor")]
public sealed record SetTrackTargetCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Arg(1, "true to target it")] bool On) : ICommand;
