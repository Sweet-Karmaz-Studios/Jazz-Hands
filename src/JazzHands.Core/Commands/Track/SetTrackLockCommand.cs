namespace JazzHands.Core.Commands;

/// <summary>Locks or unlocks a track.</summary>
/// <remarks>A locked track refuses every edit, which is what stops a stray drag ruining a finished layer.</remarks>
/// <param name="TrackId">Which track.</param>
/// <param name="Locked">True to lock it.</param>
[Command("track.set-lock", Description = "Lock or unlock a track")]
public sealed record SetTrackLockCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Arg(1, "true to lock")] bool Locked) : ICommand;
