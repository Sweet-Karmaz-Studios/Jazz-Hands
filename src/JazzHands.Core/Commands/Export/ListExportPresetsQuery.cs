using JazzHands.Core.Export;

namespace JazzHands.Core.Commands;

/// <summary>Asks which export presets there are.</summary>
[Query("export.presets", Description = "List the export presets")]
public sealed record ListExportPresetsQuery : IQuery<ExportPresetInfo[]>;
