using JazzHands.Core.Export;

namespace JazzHands.Core.Commands;

/// <summary>Asks what is in the export queue, oldest first, with progress.</summary>
[Query("export.list", Description = "List export jobs and their progress")]
public sealed record ListExportsQuery : IQuery<ExportJobInfo[]>;
