namespace JazzHands.Core.Commands;

/// <summary>Removes a role; its tracks take another.</summary>
/// <param name="Name">The role.</param>
/// <param name="To">The role its tracks take; the first remaining one when left out.</param>
[Command("role.remove", Description = "Remove a role")]
public sealed record RemoveRoleCommand(
    [property: Arg(0, "The role")] string Name,
    [property: Option("to", "The role its tracks take; the first remaining one when left out")] string? To = null) : ICommand;
