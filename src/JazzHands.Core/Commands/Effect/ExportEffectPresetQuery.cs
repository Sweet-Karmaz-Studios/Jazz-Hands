namespace JazzHands.Core.Commands;

/// <summary>A preset as JSON, for <c>effect.import-preset</c> in this project or another.</summary>
/// <param name="Preset">The preset's id or name.</param>
[Query("effect.export-preset", Description = "An effect preset as JSON")]
public sealed record ExportEffectPresetQuery(
    [property: Arg(0, "The preset's id or name")] string Preset) : IQuery<string>;
