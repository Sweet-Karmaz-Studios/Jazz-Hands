namespace JazzHands.Core.Commands;

/// <summary>Sets a track's colour on the timeline.</summary>
/// <param name="TrackId">Which track.</param>
/// <param name="Color">A hex colour such as #3A6EA5, or a name such as blue.</param>
[Command("track.set-color", Description = "Set a track's colour on the timeline")]
public sealed record SetTrackColorCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Arg(1, "#RRGGBB, #RRGGBBAA, or a name such as blue")] string Color) : ICommand;
