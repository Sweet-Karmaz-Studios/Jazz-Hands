namespace JazzHands.Core.Commands;

/// <summary>Asks what has been done, oldest first.</summary>
/// <param name="Limit">How many entries to return, counting back from the most recent.</param>
[Query("history.list", Description = "List what has been done, oldest first")]
public sealed record ListHistoryQuery(
    [property: Option("limit", "How many entries to return")] int Limit = 50) : IQuery<HistoryInfo[]>;
