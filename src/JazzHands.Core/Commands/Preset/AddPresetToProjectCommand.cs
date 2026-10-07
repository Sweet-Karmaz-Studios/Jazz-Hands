namespace JazzHands.Core.Commands;

/// <summary>Keeps an export preset in the project, so it travels with it to another machine.</summary>
/// <remarks>
/// A copy of a preset as it is now: a person's own (made with <c>presets.save</c>), a built-in one,
/// or another of the project's under a new name. In the project it comes before a person's own and
/// the built-in ones of the same name, so <c>--preset</c> means the project's wherever it is opened.
/// <c>presets.save --from</c> takes it back out to a person's own.
/// </remarks>
/// <param name="Name">The preset to keep.</param>
/// <param name="As">Keep it under this name instead, in kebab case.</param>
/// <param name="Replace">Write over a preset of the project's with that name.</param>
[Command("presets.add-to-project", Description = "Keep an export preset in the project, to travel with it")]
public sealed record AddPresetToProjectCommand(
    [property: Arg(0, "The preset to keep")] string Name,
    [property: Option("as", "Keep it under this name instead")] string? As = null,
    [property: Option("replace", "Write over one of the project's with that name")] bool Replace = false) : ICommand;
