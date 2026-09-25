using System.Collections.Immutable;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Media.Import;
using JazzHands.Media.Interop;

namespace JazzHands.Engine.Handlers;

/// <summary>Lists the files the project uses.</summary>
public sealed class ListMediaHandler : IQueryHandler<ListMediaQuery, MediaItemInfo[]>
{
    /// <inheritdoc />
    public MediaItemInfo[] Handle(Project project, ListMediaQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        string projectPath = context.Session?.ProjectPath ?? string.Empty;
        Dictionary<string, int> uses = CountUses(project);

        IEnumerable<MediaItem> items = project.Media;

        if (query.Folder is { } folder)
        {
            string wanted = MediaServices.CleanFolder(folder);

            // A folder query includes what is under it, because a bin folder is a tree and
            // nobody expects "captures" to hide "captures/day two".
            items = items.Where(item =>
                wanted.Length == 0
                || string.Equals(item.Folder, wanted, StringComparison.OrdinalIgnoreCase)
                || item.Folder.StartsWith(wanted + "/", StringComparison.OrdinalIgnoreCase));
        }

        if (query.Tag is { Length: > 0 } tag)
        {
            items = items.Where(item => item.Tags.Any(existing =>
                string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)));
        }

        if (query.Search is { Length: > 0 } search)
        {
            items = items.Where(item => Matches(item, search));
        }

        return
        [
            .. items
                .Select(item => Describe(project, item, projectPath, uses))
                .OrderBy(item => item.Folder, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Whether an item matches what was typed into the search box.
    /// </summary>
    /// <remarks>
    /// Name, file name and tags, case-insensitively, as a plain substring. Not a fuzzy match: in
    /// a bin of two hundred takes named alike, fuzzy matching returns everything and helps nobody.
    /// </remarks>
    internal static bool Matches(MediaItem item, string search) =>
        item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || item.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase)
        || item.Folder.Contains(search, StringComparison.OrdinalIgnoreCase)
        || item.Tags.Any(tag => tag.Contains(search, StringComparison.OrdinalIgnoreCase));

    internal static Dictionary<string, int> CountUses(Project project)
    {
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Sequence sequence in project.Sequences)
        {
            foreach (Track track in sequence.Tracks)
            {
                foreach (Clip clip in track.Clips)
                {
                    if (clip.MediaId is { } mediaId)
                    {
                        uses[mediaId] = uses.GetValueOrDefault(mediaId) + 1;
                    }
                }
            }
        }

        return uses;
    }

    internal static MediaItemInfo Describe(
        Project project,
        MediaItem item,
        string projectPath,
        Dictionary<string, int> uses)
    {
        string full = projectPath.Length > 0
            ? ProjectPaths.Resolve(projectPath, item.RelativePath)
            : item.RelativePath;

        MediaInfo info = item.Info ?? new MediaInfo(string.Empty, item.Duration, 0, 0);
        MediaStream? picture = info.VideoStreams.FirstOrDefault();

        return new MediaItemInfo(
            item.Id,
            item.Name,
            item.RelativePath,
            full,
            Exists(item, full),
            item.Kind,
            item.Duration,
            item.Folder,
            item.Tags,
            item.Color,
            item.Hash,
            item.Conform,
            item.Deinterlace,
            item.VfrConform,
            item.ShouldDeinterlace,
            item.ShouldConformFrameRate,
            picture?.Width ?? 0,
            picture?.Height ?? 0,
            picture?.FrameRate ?? default,
            info.IsVariableFrameRate,
            info.IsInterlaced,
            info.IsHdr,
            info.SizeBytes,
            info.Streams,
            uses.GetValueOrDefault(item.Id),
            item.ProxyPath);
    }

    /// <summary>
    /// Whether the file is where the project expects it.
    /// </summary>
    /// <remarks>
    /// An image sequence's path is a pattern rather than a file, so the first frame is what is
    /// checked. A project on an unplugged drive answers false here rather than throwing, which is
    /// what lets the bin still list everything and say which items are missing.
    /// </remarks>
    private static bool Exists(MediaItem item, string full)
    {
        if (item.Kind != MediaKind.ImageSequence || item.Sequence is not { } sequence)
        {
            return File.Exists(full);
        }

        string folder = Path.GetDirectoryName(full) ?? ".";
        return File.Exists(Path.Combine(folder, sequence.FileName(Path.GetFileName(full), 0)));
    }
}

/// <summary>Describes one media item.</summary>
public sealed class GetMediaHandler : IQueryHandler<GetMediaQuery, MediaItemInfo>
{
    /// <inheritdoc />
    public MediaItemInfo Handle(Project project, GetMediaQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        MediaItem item = MediaServices.Require(project, query.MediaId);

        return ListMediaHandler.Describe(
            project,
            item,
            context.Session?.ProjectPath ?? string.Empty,
            ListMediaHandler.CountUses(project));
    }
}

/// <summary>Reads a file and says what is in it, without importing it.</summary>
public sealed class ProbeMediaHandler : IQueryHandler<ProbeMediaQuery, MediaProbeInfo>
{
    /// <inheritdoc />
    public MediaProbeInfo Handle(Project project, ProbeMediaQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        // Relative to the project, or with no saved project to the current folder.
        string anchor = context.Session?.ProjectPath is { Length: > 0 } projectPath
            ? projectPath
            : Path.Combine(Directory.GetCurrentDirectory(), "untitled.jazz");
        string full = Path.GetFullPath(ProjectPaths.Resolve(anchor, query.Path));

        if (!File.Exists(full))
        {
            throw new CommandException("file-not-found", $"'{full}' is not there.");
        }

        MediaKind kind = MediaImporter.LooksLikeSequenceMember(full)
            ? MediaKind.ImageSequence
            : MediaKind.Movie;

        try
        {
            var probe = new Media.Probe.Prober();
            MediaInfo info = MediaImporter.Describe(probe.Probe(full));
            string hash = MediaHasher.Hash(full);

            var item = new MediaItem(
                Id.New(),
                full,
                Path.GetFileNameWithoutExtension(full),
                info.Duration,
                hash,
                Conform: query.Conform,
                Deinterlace: query.Deinterlace,
                VfrConform: query.VfrConform,
                Info: info);

            ImmutableArray<ProbeWarning> warnings =
            [
                .. MediaImporter.Warnings(item, info).Select(warning => new ProbeWarning(warning.Code, warning.Message)),
            ];

            return new MediaProbeInfo(full, kind, info, hash, new EquatableArray<ProbeWarning>(warnings));
        }
        catch (FfmpegException error)
        {
            throw new CommandException("cannot-read", $"'{full}' could not be read: {error.Message}");
        }
    }
}
