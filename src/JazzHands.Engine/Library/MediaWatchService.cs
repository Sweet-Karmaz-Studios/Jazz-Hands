using System.Collections.Concurrent;
using System.Globalization;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using Serilog;

namespace JazzHands.Engine.Library;

/// <summary>
/// Watches folders for new recordings and imports each once it has finished being written.
/// </summary>
/// <remarks>
/// <para>
/// A capture program writes a file for as long as the recording lasts. A file is taken when its
/// size has not changed between two looks a settle apart and it can be opened with nobody else
/// writing to it; then <c>media.add</c> brings it in through the session that started the watch,
/// issued as <c>watch</c>, so it is an ordinary undoable import that shows in the history. It is
/// tagged with the watch's tags, the subfolder it arrived in (OBS and ShadowPlay sort by game) and
/// the day it was recorded.
/// </para>
/// <para>
/// The project keeps its watches (<see cref="Project.Watches"/>); a session that stays open
/// (<c>Session.FollowsWatches</c>) has this service watch exactly those, keyed by folder, after
/// every change, so opening the project watches them again and undoing a watch stops it.
/// </para>
/// </remarks>
public sealed class MediaWatchService : IDisposable
{
    /// <summary>The files a watch brings in.</summary>
    public static IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".webm", ".m4v", ".avi", ".mxf", ".ts", ".mts", ".flv",
        ".wav", ".mp3", ".flac", ".aac", ".m4a", ".ogg", ".opus",
        ".png", ".jpg", ".jpeg",
    };

    private readonly ILogger _log = Log.ForContext<MediaWatchService>();
    private readonly ConcurrentDictionary<string, FolderWatch> _watches = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _settle;

    /// <summary>A service that waits <paramref name="settle"/> between looks at a growing file.</summary>
    public MediaWatchService(TimeSpan? settle = null) => _settle = settle ?? TimeSpan.FromSeconds(2);

    /// <summary>The folders being watched.</summary>
    public MediaWatchInfo[] List() =>
        [.. _watches.Values.OrderBy(watch => watch.Folder, StringComparer.OrdinalIgnoreCase).Select(watch => watch.Describe())];

    /// <summary>Starts watching a folder, or changes an existing watch's tags and bin.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="tags">Tags for what it brings in.</param>
    /// <param name="bin">The bin folder they go in.</param>
    /// <param name="later">How to run the import: the session's queue.</param>
    public void Watch(string folder, IReadOnlyList<string> tags, string bin, Func<ICommand, string, Task<CommandResult>> later)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(later);
        string full = Path.GetFullPath(folder);
        if (!Directory.Exists(full))
        {
            throw new CommandException("folder-not-found", $"'{full}' is not a folder.", "folder");
        }

        if (_watches.TryRemove(full, out FolderWatch? old))
        {
            old.Dispose();
        }

        _watches[full] = new FolderWatch(full, tags, bin, later, _settle, _log);
        _log.Information("Watching {Folder} for new recordings", full);
    }

    /// <summary>
    /// Watches exactly these folders: starts the ones not watched yet, changes the tags and bin of
    /// those that differ, and stops the rest. A folder that is not there is left out, and said.
    /// </summary>
    /// <param name="wanted">The folders, full paths, with their tags and bins: the project's watches.</param>
    /// <param name="later">How to run the imports: the session's queue.</param>
    public void Follow(IReadOnlyList<(string Folder, IReadOnlyList<string> Tags, string Bin)> wanted, Func<ICommand, string, Task<CommandResult>> later)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(later);

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string folder, IReadOnlyList<string> tags, string bin) in wanted)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!Directory.Exists(full))
            {
                _log.Warning("The project watches {Folder}, which is not there, so it is not watched", full);
                continue;
            }

            keep.Add(full);
            if (_watches.TryGetValue(full, out FolderWatch? running) && running.Describe() is { } now && now.Tags.SequenceEqual(tags) && now.Bin == bin)
            {
                continue;
            }

            Watch(full, tags, bin, later);
        }

        foreach (string stale in _watches.Keys.Where(key => !keep.Contains(key)).ToArray())
        {
            Unwatch(stale);
        }
    }

    /// <summary>Stops watching a folder, or every folder when null; how many stopped.</summary>
    public int Unwatch(string? folder)
    {
        string[] keys = folder is null ? [.. _watches.Keys] : [Path.GetFullPath(folder)];
        int stopped = 0;
        foreach (string key in keys)
        {
            if (_watches.TryRemove(key, out FolderWatch? watch))
            {
                watch.Dispose();
                stopped++;
            }
        }

        return stopped;
    }

    /// <summary>The tags a watch gives a file: its own, the subfolder it is in, and the day it was made.</summary>
    public static IReadOnlyList<string> TagsFor(string root, string file, IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var result = new List<string>(tags);
        string? parent = Path.GetDirectoryName(Path.GetFullPath(file));
        if (parent is not null && !string.Equals(parent.TrimEnd('\\'), Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            result.Add(Path.GetFileName(parent));
        }

        result.Add(File.GetCreationTime(file).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return [.. result.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <inheritdoc />
    public void Dispose() => Unwatch(null);

    /// <summary>One watched folder.</summary>
    private sealed class FolderWatch : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _timer;
        private readonly ConcurrentDictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<ICommand, string, Task<CommandResult>> _later;
        private readonly ILogger _log;
        private readonly Lock _gate = new();
        private int _imported;
        private bool _disposed;

        public FolderWatch(string folder, IReadOnlyList<string> tags, string bin, Func<ICommand, string, Task<CommandResult>> later, TimeSpan settle, ILogger log)
        {
            Folder = folder;
            Tags = tags;
            Bin = bin;
            _later = later;
            _log = log;
            _watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Created += (_, e) => Seen(e.FullPath);
            _watcher.Changed += (_, e) => Seen(e.FullPath);
            _watcher.Renamed += (_, e) => Seen(e.FullPath);
            _watcher.EnableRaisingEvents = true;
            _timer = new Timer(_ => Look(), null, settle, settle);
        }

        public string Folder { get; }

        public IReadOnlyList<string> Tags { get; }

        public string Bin { get; }

        public MediaWatchInfo Describe() => new(Folder, [.. Tags], Bin, Volatile.Read(ref _imported), _pending.Count);

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
            }

            _watcher.Dispose();
            _timer.Dispose();
        }

        private void Seen(string path)
        {
            if (Extensions.Contains(Path.GetExtension(path)) && File.Exists(path))
            {
                // -1 until the first look records a size; a file is taken only once it holds still.
                _pending.TryAdd(path, -1);
            }
        }

        /// <summary>Takes every pending file that has held its size since the last look and is free.</summary>
        private void Look()
        {
            foreach ((string path, long last) in _pending)
            {
                long size;
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    _pending.TryRemove(path, out _);
                    continue;
                }

                if (size != last || size == 0 || !Free(path))
                {
                    _pending[path] = size;
                    continue;
                }

                _pending.TryRemove(path, out _);
                _ = Import(path);
            }
        }

        private async Task Import(string path)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
            }

            try
            {
                var command = new AddMediaCommand([path], Folder: Bin, Tags: [.. TagsFor(Folder, path, Tags)]);
                CommandResult result = await _later(command, "watch").ConfigureAwait(false);
                if (result.Ok)
                {
                    Interlocked.Increment(ref _imported);
                    _log.Information("Brought in {File} from the watched folder", Path.GetFileName(path));
                }
                else if (result.Code != "already-imported")
                {
                    _log.Warning("{File} from the watched folder was not brought in: {Error}", Path.GetFileName(path), result.Error);
                }
            }
            catch (ObjectDisposedException)
            {
                // The session went; the watch goes with it at the next dispose.
            }
        }

        /// <summary>True when nothing else has the file open for writing.</summary>
        private static bool Free(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return true;
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
    }
}
