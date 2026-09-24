namespace JazzHands.Core.Commands;

/// <summary>Deletes a preset from the project. Effects applied from it stay.</summary>
/// <param name="Preset">The preset's id or name.</param>
[Command("effect.remove-preset", Description = "Delete an effect preset")]
public sealed record RemoveEffectPresetCommand(
    [property: Arg(0, "The preset's id or name")] string Preset) : ICommand;
