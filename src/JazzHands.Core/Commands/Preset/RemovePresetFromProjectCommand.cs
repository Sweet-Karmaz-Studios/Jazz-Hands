namespace JazzHands.Core.Commands;

/// <summary>Takes an export preset out of the project; a person's own or a built-in one of the same name is used again.</summary>
/// <param name="Name">The project's preset.</param>
[Command("presets.remove-from-project", Description = "Take an export preset out of the project")]
public sealed record RemovePresetFromProjectCommand(
    [property: Arg(0, "The project's preset")] string Name) : ICommand;
