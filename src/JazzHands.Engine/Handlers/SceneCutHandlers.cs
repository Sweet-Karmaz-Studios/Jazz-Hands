using System.Globalization;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Caching;
using JazzHands.Engine.Commands;
using JazzHands.Media.Analysis;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Engine.Handlers;

/// <summary>What the scene-cut commands share: the measurements, the cuts, and where a clip shows them.</summary>
internal static class SceneCutHelp
{
    /// <summary>The colour scene-cut markers get unless told otherwise.</summary>
    internal const string MarkerColor = "#5AC8FA";

    /// <summary>The service, from the session or made for this one command.</summary>
    internal static SceneCutService Service(IServiceProvider? services) =>
        services?.GetService<SceneCutService>() ?? new SceneCutService(services?.GetService<Media.Import.CacheManager>());

    /// <summary>The cuts in a media item's video stream, measured or from the cache.</summary>
    internal static IReadOnlyList<SceneCut> Cuts(
        MediaItem item,
        int? streamIndex,
        double threshold,
        Flicks? minShot,
        string projectPath,
        IServiceProvider? services,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(threshold) || threshold is < 0 or > 100)
        {
            throw new CommandException("invalid-value", "The threshold is 0 to 100: the percentage of the picture that has to change.", "threshold");
        }

        if (minShot is { } shortest && shortest < Flicks.Zero)
        {
            throw new CommandException("invalid-value", "The shortest shot cannot be negative.", "min-shot");
        }

        return Measurements(item, streamIndex, projectPath, services, cancellationToken).Cuts(threshold, minShot ?? SceneCuts.DefaultMinShot);
    }

    /// <summary>The measurements of a media item's video stream, from the cache or read from its file.</summary>
    internal static SceneMeasurements Measurements(MediaItem item, int? streamIndex, string projectPath, IServiceProvider? services, CancellationToken cancellationToken)
    {
        if (item.Kind != MediaKind.Movie)
        {
            throw new CommandException("not-a-movie", $"'{item.Name}' is a still picture, so it has no shots to find.");
        }

        MediaStream stream = StabilizeHelp.VideoStream(item, streamIndex);
        SceneCutService service = Service(services);
        if (service.Cached(item.Hash, stream.Index) is { } known)
        {
            return known;
        }

        string path = HandlerHelp.Resolve(projectPath, item.RelativePath);
        if (!File.Exists(path))
        {
            throw new CommandException("media-missing", $"'{item.Name}' is not at {path}, so its shots cannot be found. Relink it first.");
        }

        try
        {
            return service.Measure(item, path, stream, progress: null, cancellationToken);
        }
        catch (Media.Interop.FfmpegException exception)
        {
            throw new CommandException("analysis-failed", $"The shots of '{item.Name}' could not be found: {exception.Message}");
        }
    }

    /// <summary>The video stream a clip means: its own, or its file's first when it plays sound.</summary>
    internal static int? VideoStreamOf(MediaItem item, Clip clip) =>
        item.Info?.Streams.FirstOrDefault(stream => stream.Index == clip.SourceStreamIndex)?.Kind == MediaStreamKind.Video
            ? clip.SourceStreamIndex
            : null;

    /// <summary>The media item a clip plays, refusing anything that is not a video file.</summary>
    internal static MediaItem MediaOf(Project project, Clip clip) =>
        clip.MediaId is { } mediaId && MediaServices.Require(project, mediaId) is { Kind: MediaKind.Movie } item
            ? item
            : throw new CommandException("not-media", "Only a clip of a video file has shots to find.");

    /// <summary>
    /// The timeline frames inside a clip where it starts showing a new shot: the first frame on the
    /// sequence's grid whose source time is past a cut, whatever the speed, direction or curve.
    /// </summary>
    internal static List<Flicks> TimesIn(Clip clip, IReadOnlyList<SceneCut> cuts, Rational sequenceRate, Rational? sourceRate)
    {
        var times = new List<Flicks>();
        if (cuts.Count == 0 || clip.IsHold)
        {
            return times;
        }

        // A frame shows the source frame whose time it has reached; a quarter of a source frame of
        // slack keeps a rounding flick at 30000/1001 from showing the old shot one frame too long.
        Flicks slack = Flicks.FromFrames(1, sourceRate is { IsZero: false } rate ? rate : new Rational(30, 1)) / 4;
        long[] starts = [.. cuts.Select(cut => cut.Time.Value)];
        int ShotAt(Flicks timeline)
        {
            long source = (clip.SourceTimeAt(timeline) + slack).Value;
            int index = Array.BinarySearch(starts, source);
            return index >= 0 ? index + 1 : ~index;
        }

        long frame = (long)Math.Ceiling(clip.Start.ToSeconds() * sequenceRate.ToDouble());
        while (Flicks.FromFrames(frame, sequenceRate) <= clip.Start)
        {
            frame++;
        }

        int previous = ShotAt(clip.Start);
        for (Flicks at = Flicks.FromFrames(frame, sequenceRate); at < clip.End; at = Flicks.FromFrames(++frame, sequenceRate))
        {
            int shot = ShotAt(at);
            if (shot != previous)
            {
                times.Add(at);
                previous = shot;
            }
        }

        return times;
    }

    /// <summary>The source frame rate of a media item's video, for timecode.</summary>
    internal static Rational RateOf(MediaItem item, int? streamIndex) =>
        item.Info?.Streams.FirstOrDefault(stream => stream.Kind == MediaStreamKind.Video && (streamIndex is null || stream.Index == streamIndex))?.FrameRate
        ?? new Rational(30, 1);

    /// <summary>A marker for a scene cut, numbered.</summary>
    internal static Marker Marker(Flicks time, int number, SceneCutKind kind, string color) => new(
        Id.New(),
        time,
        Flicks.Zero,
        kind == SceneCutKind.Dissolve ? $"Dissolve {number}" : $"Cut {number}",
        color,
        Kind: MarkerKind.SceneCut);
}

/// <summary>Answers <c>media.detect-cuts</c>.</summary>
public sealed class DetectCutsHandler : IQueryHandler<DetectCutsQuery, SceneCutInfo[]>
{
    /// <inheritdoc />
    public SceneCutInfo[] Handle(Project project, DetectCutsQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        MediaItem item = MediaServices.Require(project, query.MediaId);
        IReadOnlyList<SceneCut> cuts = SceneCutHelp.Cuts(item, query.Stream, query.Threshold, query.MinShot, context.Session?.ProjectPath ?? string.Empty, context.Services, CancellationToken.None);
        Rational rate = SceneCutHelp.RateOf(item, query.Stream);

        return [.. cuts.Select(cut => new SceneCutInfo(cut.Time, Timecode.Format(cut.Time, rate), Math.Round(cut.Score, 2), cut.Kind))];
    }
}

/// <summary>Splits a clip, and the clips linked to it, at the shot changes in its video.</summary>
public sealed class SplitClipAtCutsHandler : ICommandHandler<SplitClipAtCutsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SplitClipAtCutsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);
        Clip clip = found.Clip;
        MediaItem item = SceneCutHelp.MediaOf(project, clip);
        int? stream = SceneCutHelp.VideoStreamOf(item, clip);

        IReadOnlyList<SceneCut> cuts = SceneCutHelp.Cuts(item, stream, command.Threshold, command.MinShot, context.ProjectPath, context.Services, context.Cancellation);
        List<Flicks> times = SceneCutHelp.TimesIn(clip, cuts, project.SettingsFor(found.Sequence).FrameRate, SceneCutHelp.RateOf(item, stream));
        if (times.Count == 0)
        {
            return project;
        }

        // Latest first, so the part before each time keeps the original id and still holds every
        // earlier one; when asked, the clips linked to it on unlocked tracks are cut at each time
        // too, and each shot's pieces are linked to each other rather than to every other shot's.
        Sequence before = found.Sequence;
        Sequence after = before;
        foreach (Flicks at in Enumerable.Reverse(times))
        {
            after = HandlerContext.Require(EditOps.SplitLinked(after, clip.Id, at, Id.New(), Id.New, command.Linked));
        }

        context.Changed(EditOps.Changed(before, after));
        return project.ReplaceSequence(after);
    }
}

/// <summary>Marks the shot changes on a clip or on a media item.</summary>
public sealed class AddMarkersAtCutsHandler : ICommandHandler<AddMarkersAtCutsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddMarkersAtCutsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        string color = command.Color is { Length: > 0 } given ? CommandValues.ParseColor(given) : SceneCutHelp.MarkerColor;

        if (project.FindClip(command.TargetId) is { } found)
        {
            HandlerHelp.RequireUnlocked(found.Track);
            Clip clip = found.Clip;
            MediaItem item = SceneCutHelp.MediaOf(project, clip);
            int? stream = SceneCutHelp.VideoStreamOf(item, clip);
            IReadOnlyList<SceneCut> cuts = SceneCutHelp.Cuts(item, stream, command.Threshold, command.MinShot, context.ProjectPath, context.Services, context.Cancellation);
            List<Flicks> times = SceneCutHelp.TimesIn(clip, cuts, project.SettingsFor(found.Sequence).FrameRate, SceneCutHelp.RateOf(item, stream));

            // The kind of each change is the kind of the cut nearest the source time it shows.
            Marker[] added = [.. times.Select((at, index) => SceneCutHelp.Marker(
                at - clip.Start,
                index + 1,
                cuts.MinBy(cut => Math.Abs((cut.Time - clip.SourceTimeAt(at)).Value))!.Kind,
                color))];
            EquatableArray<Marker> markers = [.. clip.Markers.Where(marker => marker.Kind != MarkerKind.SceneCut), .. added];
            if (markers == clip.Markers)
            {
                return project;
            }

            context.Changed(clip.Id);
            context.Changed(added.Select(marker => marker.Id));
            return project.ReplaceTrack(found.Track.ReplaceClip(clip with { Markers = [.. markers.OrderBy(marker => marker.Time)] }));
        }

        if (project.MediaItem(command.TargetId) is { } media)
        {
            IReadOnlyList<SceneCut> cuts = SceneCutHelp.Cuts(media, null, command.Threshold, command.MinShot, context.ProjectPath, context.Services, context.Cancellation);
            Marker[] added = [.. cuts.Select((cut, index) => SceneCutHelp.Marker(cut.Time, index + 1, cut.Kind, color))];
            EquatableArray<Marker> markers = [.. media.Markers.Where(marker => marker.Kind != MarkerKind.SceneCut), .. added];
            if (markers == media.Markers)
            {
                return project;
            }

            context.Changed(media.Id);
            context.Changed(added.Select(marker => marker.Id));
            return project.WithMedia(media with { Markers = [.. markers.OrderBy(marker => marker.Time)] });
        }

        throw new CommandException("not-found", $"No clip or media item has the id '{command.TargetId}'.");
    }
}

/// <summary>Makes a subclip of a stretch of a file.</summary>
public sealed class AddSubclipHandler : ICommandHandler<AddSubclipCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddSubclipCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        MediaItem item = MediaServices.Require(project, command.MediaId);
        string id = HandlerHelp.IdOr(command.NewMediaId);
        HandlerHelp.RequireUnused(project, id);
        MediaItem subclip = SubclipHelp.Make(item, id, command.In, command.Out, command.Name, command.Folder);
        context.Changed(subclip.Id);
        return project.WithMedia(subclip);
    }
}

/// <summary>Makes a subclip of every shot in a file.</summary>
public sealed class SubclipsFromCutsHandler : ICommandHandler<SubclipsFromCutsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SubclipsFromCutsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        MediaItem item = MediaServices.Require(project, command.MediaId);
        IReadOnlyList<SceneCut> cuts = SceneCutHelp.Cuts(item, null, command.Threshold, command.MinShot, context.ProjectPath, context.Services, context.Cancellation);

        // A subclip's shots are the ones inside it; a file's run from its start to its end.
        Flicks start = item.DefaultIn;
        Flicks end = item.DefaultOut;
        Flicks[] edges = [start, .. cuts.Select(cut => cut.Time).Where(time => time > start && time < end), end];
        string folder = command.Folder ?? (item.Folder.Length > 0 ? $"{item.Folder}/{item.Name}" : item.Name);
        int digits = Math.Max(2, (edges.Length - 1).ToString(CultureInfo.InvariantCulture).Length);

        for (int shot = 0; shot + 1 < edges.Length; shot++)
        {
            string name = $"{item.Name} shot {(shot + 1).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0')}";
            MediaItem subclip = SubclipHelp.Make(item, Id.New(), edges[shot], edges[shot + 1], name, folder);
            project = project.WithMedia(subclip);
            context.Changed(subclip.Id);
        }

        return project;
    }
}

/// <summary>Making a subclip item.</summary>
internal static class SubclipHelp
{
    /// <summary>A media item for a stretch of another's file, checked.</summary>
    internal static MediaItem Make(MediaItem item, string id, Flicks from, Flicks to, string? name, string? folder)
    {
        if (item.Kind == MediaKind.Still)
        {
            throw new CommandException("not-a-movie", $"'{item.Name}' is a still picture; a subclip is a stretch of something that runs.");
        }

        if (from < Flicks.Zero || to > item.Duration)
        {
            throw new CommandException("time-out-of-range", $"A subclip has to be inside '{item.Name}', which runs {Timecode.FormatClock(item.Duration)}.");
        }

        if (to <= from)
        {
            throw new CommandException("invalid-value", "A subclip's out has to be after its in.", "out");
        }

        Rational rate = SceneCutHelp.RateOf(item, null);
        return item with
        {
            Id = id,
            Name = name is { Length: > 0 } given ? given : $"{item.Name} {Timecode.Format(from, rate)}",
            Folder = folder ?? item.Folder,
            Subclip = new SubclipRange(item.Subclip?.ParentId ?? item.Id, from, to),
            Markers = [.. item.Markers.Where(marker => marker.Time >= from && marker.Time < to)],
            Tags = item.Tags,
        };
    }
}
