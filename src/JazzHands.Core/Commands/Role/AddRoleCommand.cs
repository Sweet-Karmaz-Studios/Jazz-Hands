namespace JazzHands.Core.Commands;

/// <summary>Adds a role of the project's own, for tracks to have: a language's dialogue, a narrator, crowd.</summary>
/// <param name="Name">Its name; unique, compared without case.</param>
/// <param name="Color">Its colour, as a hex string or a name.</param>
[Command("role.add", Description = "Add a role")]
public sealed record AddRoleCommand(
    [property: Arg(0, "The role's name")] string Name,
    [property: Option("color", "Its colour, as a hex string or a name")] string? Color = null) : ICommand;
