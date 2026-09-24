namespace JazzHands.Core.Commands;

/// <summary>Adds a preset from the JSON <c>effect.export-preset</c> returned, from this project or another.</summary>
/// <param name="Data">The preset's JSON.</param>
/// <param name="Name">A new name for it; its own when not given.</param>
[Command("effect.import-preset", Description = "Add an effect preset from JSON")]
public sealed record ImportEffectPresetCommand(
    [property: Option("data", "What effect.export-preset returned")] string Data,
    [property: Option("name", "A new name; its own when not given")] string? Name = null) : ICommand;
