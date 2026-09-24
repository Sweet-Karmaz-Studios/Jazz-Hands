using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Saves effects as a named preset in the project. An owner's id saves its whole chain.</summary>
/// <param name="Ids">Effect ids, or the id of a clip or track for all of its effects.</param>
/// <param name="Name">What to call it.</param>
/// <param name="PresetId">The identifier for the preset.</param>
[Command("effect.save-preset", Description = "Save an effect chain as a preset")]
public sealed record SaveEffectPresetCommand(
    [property: Arg(0, "Comma-separated effect ids, or a clip or track id for its whole chain")] EquatableArray<string> Ids,
    [property: Option("name", "What to call the preset")] string Name,
    [property: Option("id", "The identifier for the preset")] string? PresetId = null) : ICommand;
