using System.Collections.Immutable;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Media.Import;
using Serilog;

namespace JazzHands.Engine.Library;

/// <summary>
/// The project's media as files on disk: which are there, which went missing and where they might
/// have gone, which changed, and which parts of each the clips use.
/// </summary>
/// <remarks>
/// <para>
/// A missing file is looked for below the folders given, the project's folder and the folder it
/// was last in. The hash (<see cref="MediaHasher"/>) includes the file's size, so only files of the
/// same size are worth hashing: those that hash the same are the file itself, moved or copied; the
/// same name and size with another hash is the file touched since; the same name alone is only a
/// suggestion. Hashing reads eight megabytes a file, so a folder of recordings is searched in
/// seconds.
/// </para>
/// <para>
/// What the clips use is counted over every sequence, with the source a transition reads beyond a
/// clip's edge, so trimming to it keeps every frame the project can show.
/// </para>
/// </remarks>
public static class MediaLibrary
{
    /// <summary>More files than this under the search folders and the search stops, rather than walk a whole drive.</summary>
    public const int MaxFilesSearched = 200_000;

    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    /// <summary>Every media file, checked: there, missing, or changed.</summary>
    public static MediaCheckInfo[] Check(Project project, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        return [.. project.Media.Select(item =>
        {
            string path = FullPath(projectPath, item);
            MediaFileState state = !Exists(item, path) ? MediaFileState.Missing
                : Changed(item, path) ? MediaFileState.Changed
                : MediaFileState.Online;
            return new MediaCheckInfo(item.Id, item.Name, path, state);
        })];
    }

    /// <summary>The missing media, each with the files that might be it, best first.</summary>
    /// <param name="project">The project.</param>
    /// <param name="projectPath">Where it lives, or empty.</param>
    /// <param name="search">Folders to look in, with their subfolders.</param>
    /// <param name="mediaId">Only this item, or every missing one.</param>
    public static MissingMediaInfo[] FindMissing(Project project, string projectPath, IEnumerable<string> search, string? mediaId = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(search);

        MediaItem[] missing = [.. project.Media.Where(item =>
            (mediaId is null || item.Id == mediaId) && !Exists(item, FullPath(projectPath, item)))];
        if (missing.Length == 0)
        {
            return [];
        }

        // Where to look: what was asked, the project's folder, and the folders the files were last in.
        var roots = new List<string>();
        void Root(string? folder)
        {
            if (folder is { Length: > 0 } && Directory.Exists(folder) && !roots.Any(known => IsUnder(folder, known)))
            {
                roots.RemoveAll(known => IsUnder(known, folder));
                roots.Add(Path.GetFullPath(folder));
            }
        }

        foreach (string folder in search)
        {
            Root(ProjectRelative(projectPath, folder));
        }

        if (projectPath.Length > 0)
        {
            Root(Path.GetDirectoryName(Path.GetFullPath(projectPath)));
        }

        foreach (MediaItem item in missing)
        {
            Root(NearestExisting(Path.GetDirectoryName(FullPath(projectPath, item))));
        }

        // One walk over everything, indexed by name and by size.
        var byName = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
        var bySize = new Dictionary<long, List<FileInfo>>();
        int seen = 0;
        foreach (string root in roots.TakeWhile(_ => seen <= MaxFilesSearched))
        {
            foreach (string path in Directory.EnumerateFiles(root, "*", Recursive))
            {
                if (++seen > MaxFilesSearched)
                {
                    Log.ForContext(typeof(MediaLibrary)).Warning("Stopped looking for missing media after {Count} files", MaxFilesSearched);
                    break;
                }

                var file = new FileInfo(path);
                Add(byName, file.Name, file);
                Add(bySize, file.Length, file);
            }
        }

        var hashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? HashOf(FileInfo file)
        {
            if (!hashes.TryGetValue(file.FullName, out string? hash))
            {
                try
                {
                    hash = MediaHasher.Hash(file.FullName);
                }
                catch (IOException)
                {
                    hash = null;
                }
                catch (UnauthorizedAccessException)
                {
                    hash = null;
                }

                hashes[file.FullName] = hash;
            }

            return hash;
        }

        Dictionary<string, int> clips = ClipCounts(project);
        return [.. missing.Select(item =>
        {
            string name = Path.GetFileName(FullPath(projectPath, item));
            long size = item.Info?.SizeBytes ?? 0;
            var candidates = new List<MediaCandidate>();

            if (size > 0 && bySize.TryGetValue(size, out List<FileInfo>? sameSize))
            {
                foreach (FileInfo file in sameSize)
                {
                    if (item.Hash.Length > 0 && string.Equals(HashOf(file), item.Hash, StringComparison.Ordinal))
                    {
                        candidates.Add(new MediaCandidate(file.FullName, MediaMatch.Hash, file.Length));
                    }
                    else if (string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(new MediaCandidate(file.FullName, MediaMatch.NameAndSize, file.Length));
                    }
                }
            }

            if (byName.TryGetValue(name, out List<FileInfo>? sameName))
            {
                foreach (FileInfo file in sameName.Where(file => !candidates.Any(known => string.Equals(known.Path, file.FullName, StringComparison.OrdinalIgnoreCase))))
                {
                    candidates.Add(new MediaCandidate(file.FullName, MediaMatch.Name, file.Length));
                }
            }

            return new MissingMediaInfo(
                item.Id,
                item.Name,
                FullPath(projectPath, item),
                clips.GetValueOrDefault(item.Id),
                [.. candidates.OrderBy(candidate => candidate.Match).ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)]);
        })];
    }

    /// <summary>The file auto relink takes for a missing item: a hash match, else the only name and size match.</summary>
    public static MediaCandidate? AutoChoice(MissingMediaInfo missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        MediaCandidate[] hashes = [.. missing.Candidates.Where(candidate => candidate.Match == MediaMatch.Hash)];
        if (hashes.Length > 0)
        {
            return hashes[0];
        }

        MediaCandidate[] likely = [.. missing.Candidates.Where(candidate => candidate.Match == MediaMatch.NameAndSize)];
        return likely.Length == 1 ? likely[0] : null;
    }

    /// <summary>What each media item is used for.</summary>
    public static MediaUsageInfo[] Usage(Project project, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        Dictionary<string, ImmutableArray<TimeRange>> used = UsedRanges(project, Flicks.Zero);
        Dictionary<string, int> clips = ClipCounts(project);

        return [.. project.Media.Select(item =>
        {
            string[] sequences = [.. project.Sequences
                .Where(sequence => sequence.Tracks.Any(track => track.Clips.Any(clip => clip.MediaId == item.Id)))
                .Select(sequence => sequence.Name)];
            ImmutableArray<TimeRange> ranges = used.GetValueOrDefault(item.Id, []);
            Flicks total = ranges.Aggregate(Flicks.Zero, (sum, range) => sum + range.Duration);
            long bytes = item.Info?.SizeBytes ?? 0;
            if (bytes == 0 && File.Exists(FullPath(projectPath, item)))
            {
                bytes = new FileInfo(FullPath(projectPath, item)).Length;
            }

            return new MediaUsageInfo(item.Id, item.Name, clips.GetValueOrDefault(item.Id), EquatableArray.Create(sequences), total, item.Duration, bytes);
        })];
    }

    /// <summary>
    /// The stretches of each media item's source the project can show, merged, each widened by
    /// the handles and by any transition on its clips, and kept inside the file.
    /// </summary>
    public static Dictionary<string, ImmutableArray<TimeRange>> UsedRanges(Project project, Flicks handles)
    {
        ArgumentNullException.ThrowIfNull(project);
        var raw = new Dictionary<string, List<TimeRange>>(StringComparer.Ordinal);

        foreach (Sequence sequence in project.Sequences)
        {
            foreach (Track track in sequence.Tracks)
            {
                var reach = new Dictionary<string, Flicks>(StringComparer.Ordinal);
                foreach (Transition transition in track.Transitions)
                {
                    reach[transition.LeftClipId] = Flicks.Max(reach.GetValueOrDefault(transition.LeftClipId), transition.Duration);
                    reach[transition.RightClipId] = Flicks.Max(reach.GetValueOrDefault(transition.RightClipId), transition.Duration);
                }

                foreach (Clip clip in track.Clips)
                {
                    if (clip.MediaId is not { } id || project.MediaItem(id) is not { } item)
                    {
                        continue;
                    }

                    Flicks widen = handles + reach.GetValueOrDefault(clip.Id);
                    Flicks start = Flicks.Max(Flicks.Zero, clip.SourceRange.Start - widen);
                    Flicks end = clip.SourceRange.End + widen;
                    if (item.Duration > Flicks.Zero)
                    {
                        end = Flicks.Min(end, item.Duration);
                    }

                    if (end > start)
                    {
                        Add(raw, id, TimeRange.FromBounds(start, end));
                    }
                }
            }
        }

        return raw.ToDictionary(pair => pair.Key, pair => Merge(pair.Value), StringComparer.Ordinal);
    }

    /// <summary>A media item's file, as a full path.</summary>
    public static string FullPath(string projectPath, MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return projectPath.Length == 0 ? Path.GetFullPath(item.RelativePath) : ProjectPaths.Resolve(projectPath, item.RelativePath);
    }

    private static bool Exists(MediaItem item, string path) =>
        item.Kind == MediaKind.ImageSequence ? Directory.Exists(Path.GetDirectoryName(path)) : File.Exists(path);

    private static bool Changed(MediaItem item, string path)
    {
        if (item.Hash.Length == 0 || item.Kind == MediaKind.ImageSequence)
        {
            return false;
        }

        try
        {
            return !string.Equals(MediaHasher.Hash(path), item.Hash, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Dictionary<string, int> ClipCounts(Project project)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Clip clip in project.Sequences.SelectMany(sequence => sequence.Tracks).SelectMany(track => track.Clips))
        {
            if (clip.MediaId is { } id)
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        // A clip whose comp graph or light uses an item counts as one of its clips (Phase 49a).
        foreach ((string clipId, string id) in ParamReferences.All(project)
            .Where(reference => reference.Kind == ParamReferenceKind.Media && reference.Clip.MediaId != reference.Value)
            .Select(reference => (reference.Clip.Id, reference.Value))
            .Distinct())
        {
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }

        return counts;
    }

    private static ImmutableArray<TimeRange> Merge(List<TimeRange> ranges)
    {
        var merged = new List<TimeRange>();
        foreach (TimeRange range in ranges.OrderBy(range => range.Start))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                merged[^1] = TimeRange.FromBounds(merged[^1].Start, Flicks.Max(merged[^1].End, range.End));
            }
            else
            {
                merged.Add(range);
            }
        }

        return [.. merged];
    }

    private static string ProjectRelative(string projectPath, string folder) =>
        Path.IsPathRooted(folder) || projectPath.Length == 0
            ? Path.GetFullPath(folder)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, folder));

    private static string? NearestExisting(string? folder)
    {
        for (string? current = folder; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current))
            {
                // The drive root itself would search everything: stop one short of it.
                return Path.GetDirectoryName(current) is null ? null : current;
            }
        }

        return null;
    }

    private static bool IsUnder(string folder, string root)
    {
        string full = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        string parent = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        return full.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> index, TKey key, TValue value)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out List<TValue>? list))
        {
            list = [];
            index[key] = list;
        }

        list.Add(value);
    }
}
