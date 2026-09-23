namespace JazzHands.Core.Commands;

/// <summary>Changes a track's display name.</summary>
/// <param name="TrackId">Which track.</param>
/// <param name="Name">Its new name.</param>
[Command("track.rename", Description = "Rename a track")]
public sealed record RenameTrackCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Arg(1, "The new name")] string Name) : ICommand;
