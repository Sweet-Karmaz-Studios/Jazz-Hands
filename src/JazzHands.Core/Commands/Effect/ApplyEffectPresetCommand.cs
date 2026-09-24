namespace JazzHands.Core.Commands;

/// <summary>Adds a preset's effects to a clip or a track, with new ids.</summary>
/// <param name="OwnerId">The clip or track.</param>
/// <param name="Preset">The preset's id or name.</param>
/// <param name="Index">Where in the chain, from 0; the end when not given.</param>
[Command("effect.apply-preset", Description = "Apply an effect preset to a clip or a track")]
public sealed record ApplyEffectPresetCommand(
    [property: Arg(0, "The clip or track id")] string OwnerId,
    [property: Arg(1, "The preset's id or name")] string Preset,
    [property: Option("index", "Where in the chain, from 0; the end when not given")] int? Index = null) : ICommand;
