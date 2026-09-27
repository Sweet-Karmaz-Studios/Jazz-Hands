namespace JazzHands.Core.Commands;

/// <summary>Renames a role; its tracks keep it under the new name.</summary>
/// <param name="Name">The role.</param>
/// <param name="NewName">What to call it.</param>
[Command("role.rename", Description = "Rename a role")]
public sealed record RenameRoleCommand(
    [property: Arg(0, "The role")] string Name,
    [property: Arg(1, "Its new name")] string NewName) : ICommand;
