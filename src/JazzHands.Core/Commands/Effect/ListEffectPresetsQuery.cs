namespace JazzHands.Core.Commands;

/// <summary>The effect presets saved in the project.</summary>
[Query("effect.list-presets", Description = "List the effect presets in the project")]
public sealed record ListEffectPresetsQuery : IQuery<EffectPresetInfo[]>;
