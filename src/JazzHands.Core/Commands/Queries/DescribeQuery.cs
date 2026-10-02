using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>
/// Asks for the project as a person or a model would want it told: its settings and media, one
/// sequence's tracks with their clips in order, gaps, transitions, markers, and what is wrong.
/// </summary>
/// <remarks>
/// This is Claude Code's eyes on an edit, to ask for before and after every change. At brief it
/// keeps to a budget of tokens, eliding the middle of long tracks and saying so, so a large
/// project does not flood a context; <see cref="Range"/> narrows it to a stretch instead. The
/// result carries the text and the records it was written from, for <c>--json</c>.
/// </remarks>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
/// <param name="Range">Only clips, gaps and markers inside this stretch of the sequence.</param>
/// <param name="Detail">How much to say.</param>
/// <param name="Budget">At brief, about the most tokens the text may take; 0 for no limit.</param>
[Query("describe", Description = "Describe the project for a person or a model: settings, media, a sequence's tracks and clips in order, gaps, transitions, markers and problems")]
public sealed record DescribeQuery(
    [property: Option("sequence", "Which sequence; the active one when left out")] string? SequenceId = null,
    [property: Option("range", "Only what is inside this stretch: 00:10-00:25")] TimeRange? Range = null,
    [property: Option("detail", "brief (the default, budgeted) or full (ids, sources, effects, problems)")] DescribeDetail Detail = DescribeDetail.Brief,
    [property: Option("budget", "At brief, about the most tokens to spend; 0 for no limit")] int Budget = DescribeQuery.DefaultBudget) : IQuery<ProjectDescription>
{
    /// <summary>The brief budget a typical trailer fits in.</summary>
    public const int DefaultBudget = 2000;
}

/// <summary>A described project: the text, and the records it was written from.</summary>
/// <param name="Text">The description, to read.</param>
/// <param name="Tokens">About how many tokens the text takes, estimated on the high side.</param>
/// <param name="Elided">How many clips the brief text left out to keep to its budget.</param>
/// <param name="Project">The project's settings and counts.</param>
/// <param name="Media">Its media.</param>
/// <param name="Sequences">Its sequences.</param>
/// <param name="SequenceId">The sequence described.</param>
/// <param name="Tracks">That sequence's tracks.</param>
/// <param name="Clips">Its clips, in timeline order, inside the range when one was asked for.</param>
/// <param name="Markers">Its markers, inside the range.</param>
/// <param name="Problems">What is wrong: validation issues, missing fonts and files, sound effects on the wrong owner.</param>
public sealed record ProjectDescription(
    string Text,
    int Tokens,
    int Elided,
    ProjectInfo Project,
    MediaItemInfo[] Media,
    SequenceInfo[] Sequences,
    string SequenceId,
    TrackInfo[] Tracks,
    ClipInfo[] Clips,
    MarkerInfo[] Markers,
    string[] Problems);
