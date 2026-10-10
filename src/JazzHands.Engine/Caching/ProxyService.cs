using System.Collections.Concurrent;
using System.Globalization;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Export;
using JazzHands.Media.Import;
using Serilog;

namespace JazzHands.Engine.Caching;

/// <summary>A proxy file on disk.</summary>
/// <param name="Key">The content hash of the media it stands in for, without its algorithm prefix.</param>
/// <param name="Path">The file.</param>
/// <param name="Percent">Its size against the source's, in percent.</param>
/// <param name="Bytes">Its size on disk.</param>
public sealed record ProxyFile(string Key, string Path, int Percent, long Bytes);

/// <summary>
/// Proxy files: making them through the export pipeline, finding them, and standing them in for
/// their sources during playback.
/// </summary>
/// <remarks>
/// <para>
/// A proxy is named for its source's content hash and kept in the cache's proxy folder, so it
/// follows the footage, not the project: import the same file into another project and its proxy
/// is already there. It is made by rendering a one-clip sequence of the whole file at the source's
/// rate and a fraction of its size with an intra-only preset (see <see cref="ProxyPresets"/>),
/// which means everything the export pipeline does (deinterlacing, conforming, the colour
/// pipeline) a proxy has done too, and it lines up with its source frame for frame.
/// </para>
/// <para>
/// Playback asks <see cref="Substitute"/> for each media item, and when proxies are on and there
/// is one, gets back a stand-in item: the proxy's path, its own hash (so decoders and cached
/// frames never mix the two), and one video stream at the source's rate. Export never asks;
/// its frame server has no substitute.
/// </para>
/// <para>
/// Proxies are not counted against the cache's size cap. They take minutes to make, and evicting
/// one in the middle of an edit would make playback stall without anyone having asked for that.
/// <c>proxy.remove</c> and <c>cache.clear --proxies</c> delete them.
/// </para>
/// </remarks>
public sealed class ProxyService
{
    private static readonly string[] Extensions = [".mov", ".mp4", ".mkv"];

    private readonly ILogger _log = Log.ForContext<ProxyService>();
    private readonly ConcurrentDictionary<string, ProxyFile> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (MediaItem Source, MediaItem Proxy)> _standIns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _failures = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private IExportService? _queue;
    private bool _enabled;

    /// <summary>A service keeping proxies in a folder.</summary>
    /// <param name="folder">The proxy folder.</param>
    public ProxyService(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Folder = folder;
        Refresh();
    }

    /// <summary>A service keeping proxies beside a cache.</summary>
    public ProxyService(CacheManager cache)
        : this((cache ?? throw new ArgumentNullException(nameof(cache))).ProxyFolder)
    {
    }

    /// <summary>Raised when a proxy appears or goes, with its source's hash, or when proxies are switched on or off, with null.</summary>
    public event EventHandler<string?>? Changed;

    /// <summary>Where proxies are kept.</summary>
    public string Folder { get; }

    /// <summary>True when playback uses proxies.</summary>
    public bool Enabled
    {
        get => Volatile.Read(ref _enabled);
        set
        {
            if (Volatile.Read(ref _enabled) == value)
            {
                return;
            }

            Volatile.Write(ref _enabled, value);
            Changed?.Invoke(this, null);
        }
    }

    /// <summary>Every proxy on disk.</summary>
    public IReadOnlyCollection<ProxyFile> Files => [.. _files.Values];

    /// <summary>A source's proxy, or null.</summary>
    public ProxyFile? Find(string hash) =>
        hash.Length > 0 && _files.TryGetValue(Digits(hash), out ProxyFile? file) ? file : null;

    /// <summary>
    /// Gives a source's proxy to the source's new hash, for a file that was only touched: its
    /// content, and so its proxy, is the same.
    /// </summary>
    public void Rekey(string from, string to)
    {
        if (Find(from) is not { } file)
        {
            return;
        }

        string name = Path.GetFileName(file.Path);
        string moved = Path.Combine(Folder, Digits(to) + name[Digits(from).Length..]);
        File.Move(file.Path, moved, overwrite: true);
        Refresh();
    }

    /// <summary>Looks at the folder again.</summary>
    public void Refresh()
    {
        var seen = new Dictionary<string, ProxyFile>(StringComparer.Ordinal);

        if (Directory.Exists(Folder))
        {
            foreach (string path in Directory.EnumerateFiles(Folder))
            {
                if (Parse(path) is { } file)
                {
                    seen[file.Key] = file;
                }
            }
        }

        foreach (string gone in _files.Keys.Where(hash => !seen.ContainsKey(hash)))
        {
            _files.TryRemove(gone, out _);
            _standIns.TryRemove(gone, out _);
        }

        foreach ((string hash, ProxyFile file) in seen)
        {
            if (!_files.TryGetValue(hash, out ProxyFile? known) || known != file)
            {
                _files[hash] = file;
                _standIns.TryRemove(hash, out _);
            }
        }
    }

    /// <summary>
    /// The item playback should decode in place of <paramref name="item"/>: its proxy when proxies
    /// are on and it has one, otherwise null.
    /// </summary>
    public MediaItem? Substitute(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!Enabled || Find(item.Hash) is not { } file)
        {
            return null;
        }

        if (_standIns.TryGetValue(file.Key, out var known) && ReferenceEquals(known.Source, item))
        {
            return known.Proxy;
        }

        MediaItem proxy = StandIn(item, file);
        _standIns[file.Key] = (item, proxy);
        return proxy;
    }

    /// <summary>The path a source's proxy is written to.</summary>
    public string PathFor(string hash, double scale, ProxyPresetInfo preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return Path.Combine(Folder, string.Create(CultureInfo.InvariantCulture, $"{Digits(hash)}_{Percent(scale)}{preset.Extension}"));
    }

    /// <summary>
    /// The export that makes a proxy: a project holding one sequence of the whole file, and a plan
    /// that renders it at a fraction of the size, every frame a keyframe, with no sound.
    /// </summary>
    /// <param name="item">The media.</param>
    /// <param name="mediaPath">Where its file is.</param>
    /// <param name="preset">The recipe.</param>
    /// <param name="scale">Width and height against the source's.</param>
    public (ExportPlan Plan, Project Project) Plan(MediaItem item, string mediaPath, ProxyPresetInfo preset, double scale)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(preset);

        if (item.Kind != MediaKind.Movie || item.Info?.VideoStreams.FirstOrDefault() is not { } video)
        {
            throw new CommandException("no-picture", $"'{item.Name}' has no moving picture to make a proxy of.");
        }

        MediaItem absolute = item with { RelativePath = mediaPath };
        Project project = Project.CreateNew("proxy") with { Media = EquatableArray.Create(absolute) };

        int next = 0;
        string NewId() => string.Create(CultureInfo.InvariantCulture, $"proxy{next++}");

        Sequence sequence = QuickTrimOps.Create("proxy", item.Name, absolute, project.Settings, NewId);

        // Picture only: playback reads sound from the source, which costs nothing to decode.
        sequence = sequence with { Tracks = EquatableArray.Create(sequence.Tracks.Where(track => track.Kind == TrackKind.Video).ToArray()) };
        project = project with { Sequences = EquatableArray.Create(sequence), ActiveSequenceId = sequence.Id };

        ProjectSettings settings = project.SettingsFor(sequence);
        (int width, int height) = ProxyPresets.SizeFor(settings.Width, settings.Height, scale);
        Flicks length = QuickTrimOps.Length(absolute);

        var plan = new ExportPlan(
            sequence.Id,
            preset.Name,
            ExportMode.Encode,
            PathFor(item.Hash, scale, preset),
            preset.Container,
            length,
            EquatableArray.Create(new TimeRange(Flicks.Zero, length)),
            Video: new ExportVideo(
                preset.Codec,
                preset.Encoders,
                width,
                height,
                settings.FrameRate,
                preset.Quality,
                Bitrate: 0,
                Speed: "fast",
                GopLength: 1,
                BFrames: 0,
                Lossless: false),
            Reasons: EquatableArray.Create(
                $"A {Percent(scale)}% proxy of '{item.Name}' ({video.Width}x{video.Height} {video.Codec}), every frame a keyframe."),
            Label: $"Proxy of {item.Name}");

        return (plan, project);
    }

    /// <summary>
    /// Makes a proxy: queued on the export queue when there is one, which returns at once with the
    /// job's id; otherwise made here, which returns when it is written.
    /// </summary>
    /// <returns>The export job's id when queued, or null when it was made here.</returns>
    public string? Generate(
        MediaItem item,
        string mediaPath,
        ProxyPresetInfo preset,
        double scale,
        IExportService? queue,
        ExportEnvironment? environment = null,
        CancellationToken cancellationToken = default)
    {
        (ExportPlan plan, Project project) = Plan(item, mediaPath, preset, scale);
        Directory.CreateDirectory(Folder);
        _failures.TryRemove(Digits(item.Hash), out _);

        if (queue is not null)
        {
            Watch(queue);
            return queue.Enqueue(plan, project, string.Empty);
        }

        try
        {
            Exporter.Run(plan, project, string.Empty, environment, progress: null, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            _failures[Digits(item.Hash)] = error.Message;
            throw;
        }

        Finished(plan.OutputPath);
        return null;
    }

    /// <summary>Deletes a source's proxies.</summary>
    /// <returns>True when there was one.</returns>
    /// <exception cref="CommandException">When a file is open, as it is while playback uses it.</exception>
    public bool Remove(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        bool removed = false;
        lock (_gate)
        {
            foreach (string path in Candidates(hash))
            {
                try
                {
                    File.Delete(path);
                    removed = true;
                }
                catch (IOException error)
                {
                    throw new CommandException(
                        "proxy-in-use",
                        $"'{Path.GetFileName(path)}' is open, most likely for playback: {error.Message} Switch proxies off and try again.");
                }
            }

            _failures.TryRemove(Digits(hash), out _);
            Refresh();
        }

        if (removed)
        {
            Changed?.Invoke(this, hash);
        }

        return removed;
    }

    /// <summary>Deletes every proxy.</summary>
    /// <returns>How many files went.</returns>
    public int RemoveAll()
    {
        int count = 0;
        foreach (string hash in _files.Keys.ToList())
        {
            count += Remove(hash) ? 1 : 0;
        }

        return count;
    }

    /// <summary>What each movie in a project has for a proxy.</summary>
    public ProxyInfo[] List(Project project, IExportService? queue)
    {
        ArgumentNullException.ThrowIfNull(project);

        ExportJobInfo[] jobs = queue?.List() ?? [];

        return
        [
            .. project.Media
                .Where(item => item.Kind == MediaKind.Movie && item.Info?.VideoStreams.Any() == true)
                .Select(item => Describe(item, jobs)),
        ];
    }

    /// <summary>The last failure making a source's proxy, or null.</summary>
    public string? FailureFor(string hash) => _failures.TryGetValue(Digits(hash), out string? error) ? error : null;

    private ProxyInfo Describe(MediaItem item, ExportJobInfo[] jobs)
    {
        bool suggested = ProxyPresets.IsWorthAProxy(item);
        string prefix = Path.Combine(Folder, Digits(item.Hash) + "_");

        ExportJobInfo? job = jobs.LastOrDefault(candidate =>
            !candidate.IsFinished
            && candidate.OutputPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (job is not null)
        {
            ProxyState state = job.State == ExportJobState.Running ? ProxyState.Running : ProxyState.Queued;
            return new ProxyInfo(item.Id, item.Name, state, suggested, job.OutputPath, Progress: job.Progress, JobId: job.Id);
        }

        if (Find(item.Hash) is { } file)
        {
            (int width, int height) = SizeOf(item, file.Percent);
            return new ProxyInfo(item.Id, item.Name, ProxyState.Ready, suggested, file.Path, width, height, file.Bytes, 1.0);
        }

        if (FailureFor(item.Hash) is { } error)
        {
            return new ProxyInfo(item.Id, item.Name, ProxyState.Failed, suggested, Error: error);
        }

        return new ProxyInfo(item.Id, item.Name, ProxyState.None, suggested);
    }

    /// <summary>Follows a queue so a finished proxy is picked up. Once per queue.</summary>
    private void Watch(IExportService queue)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_queue, queue))
            {
                return;
            }

            _queue?.Changed -= OnJobChanged;
            _queue = queue;
            queue.Changed += OnJobChanged;
        }
    }

    private void OnJobChanged(object? sender, ExportJobInfo job)
    {
        if (!job.IsFinished || !string.Equals(Path.GetDirectoryName(job.OutputPath), Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Parse(job.OutputPath) is not { } file)
        {
            return;
        }

        if (job.State == ExportJobState.Done)
        {
            Finished(job.OutputPath);
        }
        else if (job.State == ExportJobState.Failed)
        {
            _failures[file.Key] = job.Error ?? "The export failed.";
            _log.Warning("A proxy for {Hash} failed: {Error}", file.Key, job.Error);
            Changed?.Invoke(this, file.Key);
        }
    }

    /// <summary>A proxy was written: other sizes of it go, and playback hears.</summary>
    private void Finished(string path)
    {
        if (Parse(path) is not { } file)
        {
            return;
        }

        lock (_gate)
        {
            foreach (string other in Candidates(file.Key))
            {
                if (!string.Equals(Path.GetFullPath(other), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        File.Delete(other);
                    }
                    catch (IOException error)
                    {
                        _log.Warning(error, "Could not delete the replaced proxy {Path}", other);
                    }
                }
            }

            Refresh();
        }

        _log.Information("Proxy ready: {Path}", path);
        Changed?.Invoke(this, file.Key);
    }

    private IEnumerable<string> Candidates(string hash)
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        string digits = Digits(hash);
        return [.. Directory.EnumerateFiles(Folder, digits + "_*").Where(path => Parse(path)?.Key == digits)];
    }

    /// <summary>What a proxy's file name says: hash digits, then the percentage.</summary>
    private static ProxyFile? Parse(string path)
    {
        string extension = Path.GetExtension(path);
        if (!Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        string name = Path.GetFileNameWithoutExtension(path);
        int underscore = name.LastIndexOf('_');
        if (underscore <= 0
            || !int.TryParse(name.AsSpan(underscore + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int percent)
            || percent is <= 0 or > 100)
        {
            return null;
        }

        long bytes;
        try
        {
            bytes = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return null;
        }

        return new ProxyFile(name[..underscore], Path.GetFullPath(path), percent, bytes);
    }

    /// <summary>A hash without its algorithm, as a file name can hold it.</summary>
    private static string Digits(string hash)
    {
        int colon = hash.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 ? hash[(colon + 1)..] : hash;
    }

    private static int Percent(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0 || scale > 1)
        {
            throw new CommandException("invalid-value", string.Create(CultureInfo.InvariantCulture, $"A proxy's scale is more than 0 and at most 1, not {scale}."));
        }

        return Math.Max(1, (int)Math.Round(scale * 100));
    }

    private static (int Width, int Height) SizeOf(MediaItem item, int percent) =>
        item.Info?.VideoStreams.FirstOrDefault() is { Width: > 0, Height: > 0 } video
            ? ProxyPresets.SizeFor(video.Width, video.Height, percent / 100.0)
            : (0, 0);

    /// <summary>The item playback decodes in place of a source.</summary>
    private static MediaItem StandIn(MediaItem item, ProxyFile file)
    {
        MediaStream video = item.Info!.VideoStreams.First();
        (int width, int height) = SizeOf(item, file.Percent);

        // The proxy is progressive, on the source's grid and eight bit, whatever the source was:
        // the export that made it already deinterlaced and conformed it.
        var stream = new MediaStream(
            0,
            MediaStreamKind.Video,
            "h264",
            video.Duration,
            Width: width,
            Height: height,
            FrameRate: video.FrameRate,
            BitDepth: 8);

        return item with
        {
            RelativePath = file.Path,
            Hash = string.Create(CultureInfo.InvariantCulture, $"{item.Hash}#proxy{file.Percent}"),
            Deinterlace = AutoSetting.Off,
            VfrConform = AutoSetting.Off,
            Sequence = null,
            Info = item.Info with { Streams = EquatableArray.Create(stream) },
        };
    }
}
