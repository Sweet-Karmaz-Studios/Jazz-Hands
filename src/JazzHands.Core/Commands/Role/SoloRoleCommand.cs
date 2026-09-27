namespace JazzHands.Core.Commands;

/// <summary>Solos a role: while any role or track is soloed, only those are heard.</summary>
/// <param name="Name">The role.</param>
/// <param name="Solo">True to solo it.</param>
[Command("role.solo", Description = "Solo or unsolo a role")]
public sealed record SoloRoleCommand(
    [property: Arg(0, "The role")] string Name,
    [property: Arg(1, "true to solo it")] bool Solo) : ICommand;
