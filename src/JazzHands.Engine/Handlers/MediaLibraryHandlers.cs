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
/// <remarks>The copying and cutting is done before the command is queued, so edits go on meanwhile.</remarks>
public sealed class ConsolidateProjectHandler : ICommandHandler<ConsolidateProjectCommand>, IPreparingHandler<ConsolidateProjectCommand>
{
    /// <inheritdoc />
    public object? Prepare(Project project, ConsolidateProjectCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);
        return Consolidator.Run(project, context.ProjectPath, command, context.Services?.GetService(typeof(Export.ExportEnvironment)) as Export.ExportEnvironment, context.Cancellation);
    }

    /// <inheritdoc />
    public Project Handle(Project project, ConsolidateProjectCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);
        _ = context.Prepared as ConsolidateResult ?? Prepare(project, command, context);
        return project;
    }
}

/// <summary>Writes the project and its media into a zip; the open project is not changed.</summary>
/// <remarks>The gathering and zipping is done before the command is queued, so edits go on meanwhile.</remarks>
public sealed class ArchiveProjectHandler : ICommandHandler<ArchiveProjectCommand>, IPreparingHandler<ArchiveProjectCommand>
{
    /// <inheritdoc />
    public object? Prepare(Project project, ArchiveProjectCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);
        return Consolidator.Archive(project, context.ProjectPath, command, context.Services?.GetService(typeof(Export.ExportEnvironment)) as Export.ExportEnvironment, context.Cancellation);
    }

    /// <inheritdoc />
    public Project Handle(Project project, ArchiveProjectCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);
        _ = context.Prepared as ConsolidateResult ?? Prepare(project, command, context);
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

        string folder = Path.TrimEndingDirectorySeparator(HandlerHelp.Resolve(context, command.Folder));
        if (!Directory.Exists(folder))
        {
            throw new CommandException("folder-not-found", $"'{folder}' is not a folder.", "folder");
        }

        // Kept with the project; the session watches what the project names (Session.FollowsWatches).
        var watch = new MediaWatch(HandlerHelp.Store(context, folder), command.Tags, MediaServices.CleanFolder(command.Bin));
        MediaWatch[] others = [.. project.Watches.Where(existing => !WatchHelp.Same(context.ProjectPath, existing, folder))];
        context.Changed(project.Id);

        if (command.Existing && context.Later is { } later)
        {
            _ = later(new AddMediaCommand([folder], Folder: watch.Bin, Tags: command.Tags, Recursive: true), "watch");
        }

        return project with { Watches = [.. others, watch] };
    }
}

/// <summary>Watched folders as the project keeps them.</summary>
internal static class WatchHelp
{
    /// <summary>True when a kept watch is of this folder, however it was written.</summary>
    internal static bool Same(string projectPath, MediaWatch watch, string folder) =>
        string.Equals(Full(projectPath, watch), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase);

    /// <summary>A kept watch's folder on this machine.</summary>
    internal static string Full(string projectPath, MediaWatch watch) =>
        Path.TrimEndingDirectorySeparator(HandlerHelp.Resolve(projectPath, watch.Folder));
}

/// <summary>Stops watching a folder, or every folder.</summary>
public sealed class UnwatchMediaHandler : ICommandHandler<UnwatchMediaCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, UnwatchMediaCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        string? folder = command.Folder is { Length: > 0 } given ? Path.TrimEndingDirectorySeparator(HandlerHelp.Resolve(context, given)) : null;
        MediaWatch[] kept = folder is null ? [] : [.. project.Watches.Where(watch => !WatchHelp.Same(context.ProjectPath, watch, folder))];
        if (kept.Length == project.Watches.Length)
        {
            throw new CommandException("not-watching", folder is null ? "No folder is being watched." : $"'{folder}' is not being watched.");
        }

        context.Changed(project.Id);
        return project with { Watches = [.. kept] };
    }
}

/// <summary>Lists the folders the project watches, with what each has done in this process.</summary>
public sealed class ListWatchesHandler : IQueryHandler<ListWatchesQuery, MediaWatchInfo[]>
{
    /// <inheritdoc />
    public MediaWatchInfo[] Handle(Project project, ListWatchesQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);

        string projectPath = context.Session?.ProjectPath ?? string.Empty;
        MediaWatchInfo[] running = (context.Services?.GetService(typeof(MediaWatchService)) as MediaWatchService)?.List() ?? [];
        return
        [
            .. project.Watches.Select(watch =>
            {
                string folder = WatchHelp.Full(projectPath, watch);
                return running.FirstOrDefault(info => string.Equals(info.Folder, folder, StringComparison.OrdinalIgnoreCase))
                    ?? new MediaWatchInfo(folder, watch.Tags, watch.Bin, 0, 0, Watching: false);
            }),
        ];
    }
}
