namespace JazzHands.Core.Commands;

/// <summary>Gives a track a role, for stems and for muting or soloing by role.</summary>
/// <param name="TrackId">The track.</param>
/// <param name="Role">The role, one of <c>role.list</c>; empty for the one its kind and name suggest.</param>
[Command("track.set-role", Description = "Give a track a role")]
public sealed record SetTrackRoleCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Arg(1, "The role; empty for the one its name suggests")] string Role) : ICommand;
