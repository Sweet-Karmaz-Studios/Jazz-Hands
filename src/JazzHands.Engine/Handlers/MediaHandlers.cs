using System.Collections.Immutable;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Media.Import;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Engine.Handlers;

/// <summary>Brings files into the project.</summary>
/// <remarks>
/// The one handler in this phase that touches the disk, which is why it takes its importer from
/// the context rather than making one: a test gives it an importer with no cache, and the app
/// gives it one wired to the per-user cache.
/// </remarks>
public sealed class AddMediaHandler : ICommandHandler<AddMediaCommand>
{
    private readonly ILogger _log = Log.ForContext<AddMediaHandler>();

    /// <inheritdoc />
    public Project Handle(Project project, AddMediaCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (command.Paths.IsEmpty)
        {
            throw new CommandException("nothing-to-import", "Name at least one file, folder or glob.");
        }

        MediaImporter importer = MediaServices.Importer(context);

        ImmutableArray<ImportSource> sources = MediaImporter.Expand(
            command.Paths.Select(path => HandlerHelp.Resolve(context, path)),
            command.Recursive);

        if (sources.IsEmpty)
        {
            throw new CommandException(
                "nothing-matched",
                $"Nothing matched {string.Join(", ", command.Paths)}.");
        }

        var options = new ImportOptions(
            command.Folder,
            command.Tags,
            command.Color.Length > 0 ? CommandValues.ParseColor(command.Color) : string.Empty,
            command.Conform,
            command.Deinterlace,
            command.VfrConform,
            ImageFrameRate: command.Fps);

        Project updated = project;
        var failures = new List<string>();
        int added = 0;

        foreach (ImportSource source in sources)
        {
            // A project's media is keyed by content, so importing the same file twice is a no-op
            // rather than two bin entries that thumbnail and proxy themselves separately.
            try
            {
                ImportedMedia imported = importer.Import(source, options);

                if (updated.Media.FirstOrDefault(existing =>
                    string.Equals(existing.Hash, imported.Item.Hash, StringComparison.Ordinal)) is { } already)
                {
                    _log.Debug("{Path} is already in the project as {Name}", source.Path, already.Name);
                    continue;
                }

                MediaItem item = imported.Item with
                {
                    RelativePath = HandlerHelp.Store(context, imported.Item.RelativePath),
                };

                updated = updated.WithMedia(item);
                context.Changed(item.Id);
                added++;

                foreach (ImportWarning warning in imported.Warnings)
                {
                    _log.Information("{Name}: {Code}: {Message}", item.Name, warning.Code, warning.Message);
                }

                if (Core.Export.ProxyPresets.IsWorthAProxy(item))
                {
                    // The editor offers these from proxy.list's Suggested; a script reads it here.
                    _log.Information(
                        "{Name} is heavy to decode and would edit smoothly with a proxy: jazz proxy generate <project> --media {Id} (or --auto for all of them)",
                        item.Name,
                        item.Id);
                }
            }
            catch (Exception error) when (error is FfmpegException or FileNotFoundException or IOException)
            {
                failures.Add($"{Path.GetFileName(source.Path)}: {error.Message}");
            }
        }

        if (added == 0)
        {
            throw failures.Count > 0
                ? new CommandException("import-failed", $"Nothing could be imported. {string.Join("; ", failures)}")
                : new CommandException(
                    "already-imported",
                    "Everything named is already in the project. Media is matched by content, not by path.");
        }

        if (failures.Count > 0)
        {
            _log.Warning("{Count} file(s) could not be imported: {Failures}", failures.Count, string.Join("; ", failures));
        }

        context.Changed(project.Id);
        return updated;
    }
}

/// <summary>Takes a file out of the project.</summary>
public sealed class RemoveMediaHandler : ICommandHandler<RemoveMediaCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveMediaCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        MediaItem item = MediaServices.Require(project, command.MediaId);

        ClipLocation[] users =
        [
            .. project.Sequences
                .SelectMany(sequence => sequence.Tracks.Select(track => (sequence, track)))
                .SelectMany(pair => pair.track.Clips
                    .Where(clip => string.Equals(clip.MediaId, item.Id, StringComparison.Ordinal))
                    .Select(clip => new ClipLocation(pair.sequence, pair.track, clip))),
        ];

        if (users.Length > 0 && !command.WithClips)
        {
            throw new CommandException(
                "media-in-use",
                $"'{item.Name}' is played by {users.Length} clip(s). Remove them first, or pass --with-clips.");
        }

        Project updated = project;

        foreach (ClipLocation location in users)
        {
            updated = updated.ReplaceTrack(updated.FindClip(location.Clip.Id)!.Track.RemoveClip(location.Clip.Id));
            context.Changed(location.Clip.Id);
            context.Changed(location.Track.Id);
        }

        context.Changed(item.Id);
        context.Changed(project.Id);
        return updated.RemoveMedia(item.Id);
    }
}

/// <summary>Changes a media item's name, folder, tags or conform settings.</summary>
public sealed class SetMediaHandler : ICommandHandler<SetMediaCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetMediaCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        MediaItem item = MediaServices.Require(project, command.MediaId);
        MediaItem updated = item;

        if (command.Name is { } name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new CommandException("invalid-name", "A media item needs a name.");
            }

            updated = updated with { Name = name };
        }

        if (command.Folder is { } folder)
        {
            updated = updated with { Folder = MediaServices.CleanFolder(folder) };
        }

        if (command.Tags is { } tags)
        {
            updated = updated with { Tags = tags };
        }

        if (command.Color is { } color)
        {
            updated = updated with { Color = color.Length == 0 ? string.Empty : CommandValues.ParseColor(color) };
        }

        if (command.Conform is { } conform)
        {
            updated = updated with { Conform = conform };
        }

        if (command.Deinterlace is { } deinterlace)
        {
            updated = updated with { Deinterlace = deinterlace };
        }

        if (command.VfrConform is { } vfr)
        {
            updated = updated with { VfrConform = vfr };
        }

        if (updated == item)
        {
            return project;
        }

        context.Changed(item.Id);
        return project.WithMedia(updated);
    }
}

/// <summary>Points a media item at a file that has moved.</summary>
public sealed class RelinkMediaHandler : ICommandHandler<RelinkMediaCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RelinkMediaCommand command, HandlerContext context)
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

        MediaImporter importer = MediaServices.Importer(context);

        ImportedMedia imported;
        try
        {
            imported = importer.Import(
                new ImportSource(full, item.Kind == MediaKind.ImageSequence ? MediaKind.Movie : item.Kind),
                new ImportOptions(item.Folder, item.Tags, item.Color, item.Conform, item.Deinterlace, item.VfrConform));
        }
        catch (FfmpegException error)
        {
            throw new CommandException("cannot-read", $"'{full}' could not be read: {error.Message}");
        }

        // A different length would move every cut made against this item, so it is refused unless
        // the user says they meant it. A different hash is fine: relinking to a re-encode or a
        // transcode is an ordinary thing to do.
        if (!command.Force && imported.Item.Duration != item.Duration)
        {
            throw new CommandException(
                "different-duration",
                $"'{Path.GetFileName(full)}' is {Timecode.FormatClock(imported.Item.Duration)} long and "
                + $"'{item.Name}' is {Timecode.FormatClock(item.Duration)}. Every cut made against it would move. "
                + "Pass --force if that is what you want.");
        }

        context.Changed(item.Id);

        return project.WithMedia(item with
        {
            RelativePath = HandlerHelp.Store(context, full),
            Hash = imported.Item.Hash,
            Info = imported.Item.Info,
            Duration = command.Force ? imported.Item.Duration : item.Duration,
        });
    }
}

/// <summary>Reads a media item's file again and updates what the project knows.</summary>
public sealed class ReprobeMediaHandler : ICommandHandler<ReprobeMediaCommand>
{
    private readonly ILogger _log = Log.ForContext<ReprobeMediaHandler>();

    /// <inheritdoc />
    public Project Handle(Project project, ReprobeMediaCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        MediaItem[] items = command.MediaId is { Length: > 0 } id
            ? [MediaServices.Require(project, id)]
            : [.. project.Media];

        if (items.Length == 0)
        {
            throw new CommandException("no-media", "The project has no media to reprobe.");
        }

        MediaImporter importer = MediaServices.Importer(context);
        Project updated = project;
        int changed = 0;

        foreach (MediaItem item in items)
        {
            string full = HandlerHelp.Resolve(context, item.RelativePath);

            if (!File.Exists(full))
            {
                _log.Warning("{Name} is missing from {Path}, so it was not reprobed", item.Name, full);
                continue;
            }

            try
            {
                // The old probe goes first, or the import would hand it straight back. The rest of
                // what is cached (thumbnails, waveforms, a proxy) goes only if the content turns
                // out to have changed; reprobing a file nobody touched costs nothing.
                importer.Cache?.Forget(item.Hash, CacheParts.Probes);

                ImportedMedia imported = importer.Import(
                    new ImportSource(full, item.Kind == MediaKind.ImageSequence ? MediaKind.Movie : item.Kind),
                    new ImportOptions(item.Folder, item.Tags, item.Color, item.Conform, item.Deinterlace, item.VfrConform));

                MediaItem refreshed = item with
                {
                    Hash = imported.Item.Hash,
                    Info = imported.Item.Info,
                    Duration = item.Kind == MediaKind.Still ? item.Duration : imported.Item.Duration,
                };

                if (refreshed == item)
                {
                    continue;
                }

                if (refreshed.Hash != item.Hash)
                {
                    CacheHelp.ForgetContent(context.Services, importer.Cache, item.Hash);
                }

                updated = updated.WithMedia(refreshed);
                context.Changed(item.Id);
                changed++;
            }
            catch (Exception error) when (error is FfmpegException or IOException)
            {
                _log.Warning(error, "{Name} could not be reprobed", item.Name);
            }
        }

        return changed == 0 ? project : updated;
    }
}

/// <summary>The bits the media handlers share.</summary>
internal static class MediaServices
{
    /// <summary>The media item with the given id, or a coded error.</summary>
    internal static MediaItem Require(Project project, string mediaId) =>
        project.MediaItem(mediaId)
        ?? throw new CommandException("media-not-found", $"No media with id '{mediaId}'.", "/media");

    /// <summary>
    /// The importer from the context, or a plain one with no cache.
    /// </summary>
    /// <remarks>
    /// Falling back rather than failing means a test, or a headless run with no writable cache
    /// folder, still imports. It just pays for the probe every time.
    /// </remarks>
    internal static MediaImporter Importer(HandlerContext context) =>
        context.Services?.GetService(typeof(MediaImporter)) as MediaImporter ?? new MediaImporter();

    /// <summary>A bin folder path with the slashes tidied and no leading or trailing ones.</summary>
    internal static string CleanFolder(string folder) =>
        string.Join('/', folder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
