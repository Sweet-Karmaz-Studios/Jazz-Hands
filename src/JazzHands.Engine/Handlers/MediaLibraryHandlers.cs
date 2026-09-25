using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Library;
using JazzHands.Media.Import;
using JazzHands.Media.Interop;

namespace JazzHands.Engine.Handlers;

/// <summary>Takes out every media item no clip uses.</summary>
public sealed class RemoveUnusedMediaHandler : ICommandHandler<RemoveUnusedMediaCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveUnusedMediaCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        HashSet<string> used = [.. project.Sequences
            .SelectMany(sequence => sequence.Tracks)
            .SelectMany(track => track.Clips)
            .Select(clip => clip.MediaId)
            .OfType<string>()];

        Project updated = project;
        foreach (MediaItem item in project.Media.Where(item => !used.Contains(item.Id) && !(command.KeepTagged && !item.Tags.IsEmpty)))
        {
            updated = updated.RemoveMedia(item.Id);
            context.Changed(item.Id);
        }

        if (updated == project)
        {
            return project;
        }

        context.Changed(project.Id);
        return updated;
    }
}

/// <summary>Swaps a media item's file for another, keeping its clips.</summary>
public sealed class ReplaceMediaHandler : ICommandHandler<ReplaceMediaCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ReplaceMediaCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        MediaItem item = MediaServices.Require(project, command.MediaId);
        string full = HandlerHelp.Resolve(context, command.Path);
        if (!File.Exists(full))
        {
            throw new CommandException("file-not-found", $"'{full}' is not there.");
        }

        ImportedMedia imported;
        try
        {
            imported = MediaServices.Importer(context).Import(
                new ImportSource(full, item.Kind == MediaKind.ImageSequence ? MediaKind.Movie : item.Kind),
                new ImportOptions(item.Folder, item.Tags, item.Color, item.Conform, item.Deinterlace, item.VfrConform));
        }
        catch (FfmpegException error)
        {
            throw new CommandException("cannot-read", $"'{full}' could not be read: {error.Message}");
        }

        if (!command.Force && imported.Item.Kind != MediaKind.Still)
        {
            Flicks longest = project.Sequences
                .SelectMany(sequence => sequence.Tracks)
                .SelectMany(track => track.Clips)
                .Where(clip => clip.MediaId == item.Id)
                .Select(clip => clip.SourceOut)
                .DefaultIfEmpty(Flicks.Zero)
                .Max();
            if (longest > imported.Item.Duration)
            {
                throw new CommandException(
                    "too-short",
                    $"'{Path.GetFileName(full)}' is {Timecode.FormatClock(imported.Item.Duration)} long, and a clip plays '{item.Name}' up to {Timecode.FormatClock(longest)}. "
                    + "Pass --force to replace it anyway; those clips then run out of picture.");
            }
        }

        context.Changed(item.Id);
        return project.WithMedia(item with
        {
            RelativePath = HandlerHelp.Store(context, full),
            Hash = imported.Item.Hash,
            Info = imported.Item.Info,
            Kind = imported.Item.Kind,
            Duration = imported.Item.Duration,
            Sequence = imported.Item.Sequence,
        });
    }
}

/// <summary>Says which media files are there, missing or changed.</summary>
public sealed class CheckMediaHandler : IQueryHandler<CheckMediaQuery, MediaCheckInfo[]>
{
    /// <inheritdoc />
    public MediaCheckInfo[] Handle(Project project, CheckMediaQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return MediaLibrary.Check(project, context.Session?.ProjectPath ?? string.Empty);
    }
}

/// <summary>Finds where the missing media went.</summary>
public sealed class FindMissingMediaHandler : IQueryHandler<FindMissingMediaQuery, MissingMediaInfo[]>
{
    /// <inheritdoc />
    public MissingMediaInfo[] Handle(Project project, FindMissingMediaQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        if (query.MediaId is { Length: > 0 } id)
        {
            _ = MediaServices.Require(project, id);
        }

        return MediaLibrary.FindMissing(project, context.Session?.ProjectPath ?? string.Empty, query.Search, query.MediaId);
    }
}

/// <summary>Says what each media item is used for.</summary>
public sealed class MediaUsageHandler : IQueryHandler<MediaUsageQuery, MediaUsageInfo[]>
{
    /// <inheritdoc />
    public MediaUsageInfo[] Handle(Project project, MediaUsageQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        if (query.MediaId is { Length: > 0 } id)
        {
            _ = MediaServices.Require(project, id);
        }

        return [.. MediaLibrary.Usage(project, context.Session?.ProjectPath ?? string.Empty)
            .Where(usage => (query.MediaId is null || usage.MediaId == query.MediaId) && (!query.Unused || usage.Clips == 0))];
    }
}

/// <summary>Gathers the project and its media into a folder; the open project is not changed.</summary>
public sealed class ConsolidateProjectHandler : ICommandHandler<ConsolidateProjectCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ConsolidateProjectCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);
        _ = Consolidator.Run(project, context.ProjectPath, command, context.Services?.GetService(typeof(Export.ExportEnvironment)) as Export.ExportEnvironment, context.Cancellation);
        return project;
    }
}

/// <summary>Writes the project and its media into a zip; the open project is not changed.</summary>
public sealed class ArchiveProjectHandler : ICommandHandler<ArchiveProjectCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ArchiveProjectCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);
        _ = Consolidator.Archive(project, context.ProjectPath, command, context.Services?.GetService(typeof(Export.ExportEnvironment)) as Export.ExportEnvironment, context.Cancellation);
        return project;
    }
}

/// <summary>Starts watching a folder; with --existing, brings in what is there now as well.</summary>
public sealed class WatchMediaHandler : ICommandHandler<WatchMediaCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, WatchMediaCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Later is not { } later || context.Services?.GetService(typeof(MediaWatchService)) is not MediaWatchService watches)
        {
            throw new CommandException("no-session", "A watch needs a session that stays open: the editor, or jazz serve.");
        }

        string folder = HandlerHelp.Resolve(context, command.Folder);
        watches.Watch(folder, [.. command.Tags], MediaServices.CleanFolder(command.Bin), later);

        if (command.Existing)
        {
            _ = later(new AddMediaCommand([folder], Folder: MediaServices.CleanFolder(command.Bin), Tags: command.Tags, Recursive: true), "watch");
        }

        return project;
    }
}

/// <summary>Stops watching a folder, or every folder.</summary>
public sealed class UnwatchMediaHandler : ICommandHandler<UnwatchMediaCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, UnwatchMediaCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        var watches = context.Services?.GetService(typeof(MediaWatchService)) as MediaWatchService;
        string? folder = command.Folder is { Length: > 0 } given ? HandlerHelp.Resolve(context, given) : null;
        if ((watches?.Unwatch(folder) ?? 0) == 0)
        {
            throw new CommandException("not-watching", folder is null ? "No folder is being watched." : $"'{folder}' is not being watched.");
        }

        return project;
    }
}

/// <summary>Lists the folders being watched.</summary>
public sealed class ListWatchesHandler : IQueryHandler<ListWatchesQuery, MediaWatchInfo[]>
{
    /// <inheritdoc />
    public MediaWatchInfo[] Handle(Project project, ListWatchesQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (context.Services?.GetService(typeof(MediaWatchService)) as MediaWatchService)?.List() ?? [];
    }
}
