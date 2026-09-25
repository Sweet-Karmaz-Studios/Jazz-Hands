using System.Globalization;
using System.Text;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Core.Validation;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>
/// Describes the project: its settings and media, one sequence as <c>timeline.describe</c> tells
/// it, and what is wrong, kept to a budget of tokens at brief.
/// </summary>
/// <remarks>
/// The budget is met by listing fewer clips on the longest tracks, the start and end of each and a
/// line for the middle, never by dropping tracks, media or problems: a reader told that twelve
/// clips are summed up can ask for them with <c>--range</c>, but cannot ask about something it was
/// never told exists. The records the text was written from come back with it for <c>--json</c>.
/// </remarks>
public sealed class DescribeHandler : IQueryHandler<DescribeQuery, ProjectDescription>
{
    /// <summary>Clips a track may list, tried in turn until the text fits the budget.</summary>
    private static readonly int[] ClipLimits = [int.MaxValue, 48, 32, 24, 16, 12, 8, 6, 4, 2];

    /// <summary>Problems a brief description names before saying how many more there are.</summary>
    private const int BriefProblems = 5;

    /// <inheritdoc />
    public ProjectDescription Handle(Project project, DescribeQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = HandlerHelp.Sequence(project, query.SequenceId);
        string projectPath = context.Session?.ProjectPath ?? string.Empty;
        bool full = query.Detail == DescribeDetail.Full;

        ProjectInfo info = new GetProjectHandler().Handle(project, new GetProjectQuery(), context);
        MediaItemInfo[] media = new ListMediaHandler().Handle(project, new ListMediaQuery(), context);
        SequenceInfo[] sequences = new ListSequencesHandler().Handle(project, new ListSequencesQuery(), context);
        TrackInfo[] tracks = new ListTracksHandler().Handle(project, new ListTracksQuery(sequence.Id), context);
        ClipInfo[] clips = [.. new ListClipsHandler().Handle(project, new ListClipsQuery(SequenceId: sequence.Id), context)
            .Where(clip => query.Range is not { } range || new TimeRange(clip.Start, clip.Duration).Intersects(range))];
        MarkerInfo[] markers = [.. new ListMarkersHandler().Handle(project, new ListMarkersQuery(SequenceId: sequence.Id), context)
            .Where(marker => query.Range is not { } range || range.Contains(marker.TimelineTime) || (marker.Duration.Value > 0 && new TimeRange(marker.TimelineTime, marker.Duration).Intersects(range)))];
        string[] problems = Problems(project, projectPath, media);

        string text = string.Empty;
        int elided = 0;
        int limit = full || query.Budget <= 0 ? 0 : query.Budget;
        foreach (int clipLimit in ClipLimits)
        {
            string timeline = DescribeTimelineHandler.Write(project, sequence, full, query.Range, clipLimit, projectPath, out elided, problems: false);
            text = Compose(info, media, sequences, sequence, timeline, problems, full);
            if (limit == 0 || TokenEstimate.Of(text) <= limit)
            {
                break;
            }
        }

        return new ProjectDescription(text, TokenEstimate.Of(text), elided, info, media, sequences, sequence.Id, tracks, clips, markers, problems);
    }

    private static string Compose(ProjectInfo info, MediaItemInfo[] media, SequenceInfo[] sequences, Sequence sequence, string timeline, string[] problems, bool full)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"project {info.Name}");
        if (full)
        {
            text.Append(CultureInfo.InvariantCulture, $" ({info.Id}{(info.Path.Length > 0 ? $", {info.Path}" : string.Empty)})");
        }

        text.Append(CultureInfo.InvariantCulture, $": {info.Width}x{info.Height} at {info.Fps} fps, {media.Length} media, {sequences.Length} sequence(s)\n");

        if (media.Length > 0)
        {
            text.Append("\nmedia\n");
            foreach (MediaItemInfo item in media)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {item.Name}: {Describe(item)}");
                if (!item.Exists)
                {
                    text.Append(" [offline]");
                }

                text.Append('\n');
                if (full)
                {
                    text.Append(CultureInfo.InvariantCulture, $"         id {item.Id}, {item.Path}\n");
                }
            }
        }

        if (sequences.Length > 1)
        {
            text.Append("\nsequences\n");
            foreach (SequenceInfo other in sequences)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"  {other.Name}: {Timecode.FormatClock(other.Duration)}, {other.ClipCount} clips{(other.IsActive ? ", active" : string.Empty)}{(other.Id == sequence.Id ? ", described below" : string.Empty)}");
                text.Append(full ? $", id {other.Id}\n" : "\n");
            }
        }

        text.Append('\n').Append(timeline);

        if (problems.Length > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nproblems ({problems.Length})\n");
            foreach (string problem in full ? problems : problems.Take(BriefProblems))
            {
                text.Append("  ").Append(problem).Append('\n');
            }

            if (!full && problems.Length > BriefProblems)
            {
                text.Append(CultureInfo.InvariantCulture, $"  and {problems.Length - BriefProblems} more (--detail full)\n");
            }
        }

        return text.ToString();
    }

    /// <summary>A media item in a few words: kind, length, picture and sound.</summary>
    private static string Describe(MediaItemInfo item)
    {
        var parts = new List<string> { item.Kind.ToString().ToLowerInvariant(), Timecode.FormatClock(item.Duration) };
        if (item.Width > 0)
        {
            parts.Add(item.FrameRate is { } rate
                ? string.Create(CultureInfo.InvariantCulture, $"{item.Width}x{item.Height} {rate.ToDouble():0.###} fps{(item.IsVariableFrameRate ? " variable" : string.Empty)}")
                : string.Create(CultureInfo.InvariantCulture, $"{item.Width}x{item.Height}"));
        }

        int sound = item.Streams.Count(stream => stream.Kind == MediaStreamKind.Audio);
        if (sound > 0)
        {
            parts.Add(sound == 1 ? "sound" : $"{sound} sound streams");
        }

        if (item.IsHdr)
        {
            parts.Add("HDR");
        }

        if (item.UsedByClips == 0)
        {
            parts.Add("unused");
        }

        return string.Join(", ", parts);
    }

    /// <summary>Everything wrong with the project, a sentence each.</summary>
    private static string[] Problems(Project project, string projectPath, MediaItemInfo[] media) =>
    [
        .. Validator.Semantic(project)
            .Concat(Titles.TitleFonts.Missing(project, projectPath))
            .Select(issue => $"{issue.Severity.ToString().ToLowerInvariant()}: {issue.Code}: {issue.Message}"),
        .. media.Where(item => !item.Exists).Select(item => $"error: media-offline: '{item.Name}' is not at {item.FullPath}."),
    ];
}
