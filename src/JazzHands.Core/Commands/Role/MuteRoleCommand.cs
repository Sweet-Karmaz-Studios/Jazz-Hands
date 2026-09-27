namespace JazzHands.Core.Commands;

/// <summary>Silences every track of a role, or lets them be heard again.</summary>
/// <param name="Name">The role.</param>
/// <param name="Muted">True to silence it.</param>
[Command("role.mute", Description = "Mute or unmute a role")]
public sealed record MuteRoleCommand(
    [property: Arg(0, "The role")] string Name,
    [property: Arg(1, "true to mute it")] bool Muted) : ICommand;
