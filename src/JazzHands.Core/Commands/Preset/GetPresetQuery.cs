using JazzHands.Core.Export;

namespace JazzHands.Core.Commands;

/// <summary>An export preset in full, as its file has it, ready to copy and change.</summary>
/// <param name="Name">The preset.</param>
[Query("presets.get", Description = "Show an export preset in full", Standalone = true)]
public sealed record GetPresetQuery(
    [property: Arg(0, "The preset")] string Name) : IQuery<ExportPreset>;
