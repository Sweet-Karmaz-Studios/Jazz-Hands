namespace JazzHands.Core.Commands;

/// <summary>How much of the timeline to describe.</summary>
public enum DescribeDetail
{
    /// <summary>Tracks and clips, one line each. Budgeted for an LLM to read cheaply.</summary>
    Brief,

    /// <summary>Everything brief has, plus sources, speeds, effects, markers and gaps.</summary>
    Full,
}

/// <summary>Asks for a readable account of a sequence.</summary>
/// <remarks>
/// This is how Claude Code sees the edit. The output is written to be read by a person and by a
/// model: no tables, no box drawing, times as timecode, and the things that are wrong called out
/// rather than left to be inferred.
/// </remarks>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
/// <param name="Detail">How much to say.</param>
[Query("timeline.describe", Description = "Describe a sequence in readable text")]
public sealed record DescribeTimelineQuery(
    [property: Option("sequence", "Which sequence")] string? SequenceId = null,
    [property: Option("detail", "brief or full")] DescribeDetail Detail = DescribeDetail.Brief) : IQuery<string>;
