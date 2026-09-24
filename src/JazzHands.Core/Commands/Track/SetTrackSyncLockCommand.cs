namespace JazzHands.Core.Commands;

/// <summary>Turns sync lock on or off for a track.</summary>
/// <remarks>
/// A sync-locked track moves with ripple edits made on other tracks, so it stays in sync with
/// them. Tracks are sync-locked unless turned off; a music bed that should not be cut is the
/// usual one to turn off.
/// </remarks>
/// <param name="TrackId">Which track.</param>
/// <param name="SyncLocked">True to keep it in sync with ripples on other tracks.</param>
[Command("track.set-sync-lock", Description = "Keep a track in sync with ripple edits on others")]
public sealed record SetTrackSyncLockCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Arg(1, "true to keep it in sync")] bool SyncLocked) : ICommand;
