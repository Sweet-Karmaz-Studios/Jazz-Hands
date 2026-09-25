using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Media.Interop;
using JazzHands.Media.Probe;

namespace JazzHands.Engine.Handlers;

/// <summary>What the chapter commands share.</summary>
internal static class ChapterHelp
{
    /// <summary>A sequence's chapters in time order, each running to the next or to the end.</summary>
    internal static ChapterSummary[] Chapters(Sequence sequence)
    {
        Marker[] marks = [.. sequence.Markers.Where(marker => marker.IsChapter).OrderBy(marker => marker.Time)];
        Flicks end = sequence.Duration;
        return [.. marks.Select((marker, index) => new ChapterSummary(
            marker.Id,
            marker.Time,
            index + 1 < marks.Length ? marks[index + 1].Time : Flicks.Max(end, marker.Time),
            marker.Name))];
    }

    /// <summary>A chapter mark: a marker with no length and its chapter flag set.</summary>
    internal static Marker Mark(string id, Flicks at, string name) => new(id, at, Flicks.Zero, name, "#4C9AFF", IsChapter: true);
}

/// <summary>Puts a chapter on a sequence.</summary>
public sealed class AddChapterHandler : ICommandHandler<AddChapterCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddChapterCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (command.At.IsNegative)
        {
            throw new CommandException("time-out-of-range", "A chapter cannot start before the timeline does.");
        }

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);
        string id = HandlerHelp.IdOr(command.ChapterId);
        if (MarkerLookup.Find(project, id) is not null)
        {
            throw new CommandException("duplicate-id", $"'{id}' is already a marker in this project.");
        }

        context.Changed(id);
        context.Changed(sequence.Id);
        return project.ReplaceSequence(sequence with { Markers = sequence.Markers.Add(ChapterHelp.Mark(id, command.At, command.Name)) });
    }
}

/// <summary>Brings a media item's chapters onto a sequence.</summary>
public sealed class ImportChaptersHandler : ICommandHandler<ImportChaptersCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ImportChaptersCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        MediaItem item = project.MediaItem(command.MediaId)
            ?? throw new CommandException("media-not-found", $"No media item with id '{command.MediaId}'.", "/media");
        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);
        Flicks at = command.At ?? Flicks.Zero;

        // A file probed before chapters were kept is read again for them.
        IReadOnlyList<(Flicks Start, string Title)> chapters = item.Info is { Chapters.IsEmpty: false } info
            ? [.. info.Chapters.Select(chapter => (chapter.Start, chapter.Title))]
            : Probe(HandlerHelp.Resolve(context, item.RelativePath));

        if (chapters.Count == 0)
        {
            throw new CommandException("no-chapters", $"'{item.Name}' has no chapters.");
        }

        Flicks frame = Flicks.FromFrames(1, project.SettingsFor(sequence).FrameRate);
        var markers = new List<Marker>(sequence.Markers);
        foreach ((Flicks start, string title) in chapters)
        {
            Flicks time = at + start;
            if (time.IsNegative || markers.Any(marker => marker.IsChapter && Math.Abs((marker.Time - time).Value) < frame.Value))
            {
                continue;
            }

            string id = Id.New();
            markers.Add(ChapterHelp.Mark(id, time, title.Length > 0 ? title : $"Chapter {markers.Count(marker => marker.IsChapter) + 1}"));
            context.Changed(id);
        }

        if (context.ChangedIds.IsEmpty)
        {
            return project;
        }

        context.Changed(sequence.Id);
        return project.ReplaceSequence(sequence with { Markers = new EquatableArray<Marker>([.. markers]) });
    }

    private static IReadOnlyList<(Flicks Start, string Title)> Probe(string path)
    {
        try
        {
            return [.. new Prober().Probe(path, detectFrameRateMode: false).Chapters.Select(chapter => (chapter.Start, chapter.Title ?? string.Empty))];
        }
        catch (Exception error) when (error is FfmpegException or IOException)
        {
            throw new CommandException("cannot-read", error.Message);
        }
    }
}

/// <summary>Makes every sequence marker a chapter.</summary>
public sealed class ChaptersFromMarkersHandler : ICommandHandler<ChaptersFromMarkersCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ChaptersFromMarkersCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);
        if (sequence.Markers.All(marker => marker.IsChapter))
        {
            return project;
        }

        foreach (Marker marker in sequence.Markers.Where(marker => !marker.IsChapter))
        {
            context.Changed(marker.Id);
        }

        context.Changed(sequence.Id);
        return project.ReplaceSequence(sequence with
        {
            Markers = new EquatableArray<Marker>([.. sequence.Markers.Select(marker => marker with { IsChapter = true })]),
        });
    }
}

/// <summary>A sequence's chapters.</summary>
public sealed class ListChaptersHandler : IQueryHandler<ListChaptersQuery, ChapterSummary[]>
{
    /// <inheritdoc />
    public ChapterSummary[] Handle(Project project, ListChaptersQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);

        return ChapterHelp.Chapters(HandlerHelp.Sequence(project, query.SequenceId));
    }
}
