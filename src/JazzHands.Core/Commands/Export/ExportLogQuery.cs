namespace JazzHands.Core.Commands;

/// <summary>What an export has done, a line a step: when it started, on what, what it said, how it ended.</summary>
/// <param name="JobId">The job.</param>
[Query("export.log", Description = "Show what an export has done, step by step")]
public sealed record ExportLogQuery(
    [property: Arg(0, "The job id")] string JobId) : IQuery<string[]>;
