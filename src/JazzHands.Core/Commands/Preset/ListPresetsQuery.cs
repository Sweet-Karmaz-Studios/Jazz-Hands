using JazzHands.Core.Export;

namespace JazzHands.Core.Commands;

/// <summary>Asks which export presets there are: the built-in ones and a person's own.</summary>
/// <param name="Category">Only this category: youtube, discord, device, archive, web, image, audio or other.</param>
[Query("presets.list", Description = "List the export presets, built in and your own", Standalone = true)]
public sealed record ListPresetsQuery(
    [property: Option("category", "Only this category: youtube, discord, device, archive, web, image, audio or other")] string? Category = null) : IQuery<ExportPresetSummary[]>;
