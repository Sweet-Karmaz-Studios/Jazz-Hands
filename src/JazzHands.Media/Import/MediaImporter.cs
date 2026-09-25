using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Media.Probe;
using Serilog;

namespace JazzHands.Media.Import;

/// <summary>Something worth telling the user about a file they just imported.</summary>
/// <param name="Code">A stable kebab-case code.</param>
/// <param name="Message">A sentence, written for somebody who is about to start editing.</param>
public sealed record ImportWarning(string Code, string Message);

/// <summary>One file, examined and ready to become a media item.</summary>
/// <param name="Item">The media item, with its probe cached on it.</param>
/// <param name="FullPath">Where it actually is on this machine.</param>
/// <param name="Warnings">What the user should know.</param>
/// <param name="FromCache">True when the probe came from the cache rather than the file.</param>
public sealed record ImportedMedia(
    MediaItem Item,
    string FullPath,
    ImmutableArray<ImportWarning> Warnings,
    bool FromCache);

/// <summary>
/// Turns a path into a media item: hash, probe, conform decisions, and the warnings worth
/// raising before somebody starts cutting with it.
/// </summary>
/// <remarks>
/// Import is where a file stops being a file and becomes something the project knows about. It is
/// also the only place that is allowed to be slow, so it does the expensive things once: hashing,
/// probing, and working out whether the frames sit on a grid. Everything after this reads what
/// import wrote.
/// </remarks>
public sealed class MediaImporter(CacheManager? cache = null, Prober? prober = null)
{
    /// <summary>
    /// The numbered-file pattern an image sequence is recognised by.
    /// </summary>
    /// <remarks>
    /// <c>render.0001.exr</c> and <c>shot_v2.000123.png</c> both match, and the digits are the
    /// last run of them before the extension so that a file called <c>cam2.0001.png</c> is
    /// numbered by the 0001 rather than by the 2.
    /// </remarks>
    private static readonly Regex NumberedFile = new(
        @"^(?<stem>.*?)(?<number>\d+)(?<extension>\.[^.]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly HashSet<string> StillExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".exr", ".dpx", ".tga", ".webp", ".gif",
    };

    private readonly ILogger _log = Log.ForContext<MediaImporter>();
    private readonly Prober _prober = prober ?? new Prober();

    /// <summary>The cache probes are read from and written to, or null when there is none.</summary>
    public CacheManager? Cache { get; } = cache;

    /// <summary>The rate a still or an image sequence plays at when nothing says otherwise.</summary>
    public Rational DefaultImageFrameRate { get; init; } = Rational.Fps24;

    /// <summary>How long a still lasts when nothing says otherwise.</summary>
    public Flicks DefaultStillDuration { get; init; } = Flicks.FromSeconds(5);

    /// <summary>True when a file name looks like one image out of a numbered run.</summary>
    public static bool LooksLikeSequenceMember(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string name = Path.GetFileName(path);
        return StillExtensions.Contains(Path.GetExtension(path)) && NumberedFile.IsMatch(name);
    }

    /// <summary>
    /// Expands what the user asked to import into the files that will actually be imported.
    /// </summary>
    /// <remarks>
    /// A folder becomes its contents. A glob becomes its matches. A run of numbered images
    /// becomes one entry, because <c>render.%04d.exr</c> from 1 to 240 is one thing and not two
    /// hundred and forty.
    /// </remarks>
    public static ImmutableArray<ImportSource> Expand(IEnumerable<string> paths, bool recursive = false)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var files = new List<string>();

        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                files.AddRange(Directory.EnumerateFiles(
                    path,
                    "*",
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly));
                continue;
            }

            if (path.Contains('*', StringComparison.Ordinal) || path.Contains('?', StringComparison.Ordinal))
            {
                string folder = Path.GetDirectoryName(path) is { Length: > 0 } parent ? parent : ".";
                string pattern = Path.GetFileName(path);

                if (Directory.Exists(folder))
                {
                    files.AddRange(Directory.EnumerateFiles(folder, pattern));
                }

                continue;
            }

            files.Add(path);
        }

        return Group(files);
    }

    /// <summary>Examines one source and produces the media item for it.</summary>
    public ImportedMedia Import(ImportSource source, ImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        options ??= new ImportOptions();

        return source.Kind == MediaKind.ImageSequence
            ? ImportSequence(source, options)
            : ImportFile(source, options);
    }

    private ImportedMedia ImportFile(ImportSource source, ImportOptions options)
    {
        string full = Path.GetFullPath(source.Path);

        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"'{full}' is not there.", full);
        }

        string hash = MediaHasher.Hash(full);
        (MediaInfo info, bool fromCache) = ProbeWithCache(full, hash);

        bool still = source.Kind == MediaKind.Still;

        Flicks duration = still
            ? options.StillDuration ?? DefaultStillDuration
            : info.Duration;

        var item = new MediaItem(
            Id.New(),
            full,
            options.Name ?? Path.GetFileNameWithoutExtension(full),
            duration,
            hash,
            null,
            options.Tags,
            still ? MediaKind.Still : MediaKind.Movie,
            options.Folder,
            options.Color,
            options.Conform,
            options.Deinterlace,
            options.VfrConform,
            info);

        return new ImportedMedia(item, full, Warnings(item, info), fromCache);
    }

    private ImportedMedia ImportSequence(ImportSource source, ImportOptions options)
    {
        IReadOnlyList<string> members = source.Members;
        string first = Path.GetFullPath(members[0]);

        string hash = MediaHasher.HashSequence([.. members.Select(Path.GetFullPath)]);
        Rational frameRate = options.ImageFrameRate ?? DefaultImageFrameRate;

        // A sequence has no container to probe, so the first frame is probed for its size and
        // pixel format and the run supplies the timing.
        (MediaInfo frame, bool fromCache) = ProbeWithCache(first, hash);

        var sequence = new ImageSequenceInfo(source.SequenceStart, members.Count, frameRate, source.SequencePadding);

        MediaStream? picture = frame.PrimaryStream;
        MediaInfo info = frame with
        {
            Duration = sequence.Duration,
            SizeBytes = members.Sum(member => new FileInfo(member).Length),
            Streams = picture is null
                ? frame.Streams
                : EquatableArray.Create(picture with
                {
                    Duration = sequence.Duration,
                    FrameRate = frameRate,
                    IsVariableFrameRate = false,
                }),
        };

        var item = new MediaItem(
            Id.New(),
            Path.Combine(Path.GetDirectoryName(first) ?? ".", source.Pattern),
            options.Name ?? source.Stem.TrimEnd('.', '_', '-'),
            sequence.Duration,
            hash,
            null,
            options.Tags,
            MediaKind.ImageSequence,
            options.Folder,
            options.Color,
            options.Conform,
            options.Deinterlace,
            options.VfrConform,
            info,
            sequence);

        return new ImportedMedia(item, first, Warnings(item, info), fromCache);
    }

    /// <summary>
    /// What a probe is cached under: the content hash and the version of what the probe says.
    /// </summary>
    /// <remarks>
    /// Bumped whenever the prober learns something new about files it has already seen, so a
    /// probe cached before is taken again rather than trusted. Version 2 reads Matroska's per
    /// stream DURATION tag; version 3 keeps each picture stream's colour signalling (Phase 17).
    /// </remarks>
    internal static string ProbeKey(string hash) => $"{hash}#probe3";

    /// <summary>Probes a file, using the cache when it has seen this content before.</summary>
    private (MediaInfo Info, bool FromCache) ProbeWithCache(string path, string hash)
    {
        if (Cache?.GetProbe(ProbeKey(hash)) is { } cached)
        {
            try
            {
                MediaInfo? restored = JsonSerializer.Deserialize<MediaInfo>(cached, JazzJson.Options);
                if (restored is not null)
                {
                    return (restored, true);
                }
            }
            catch (JsonException error)
            {
                // A cache written by an older build. Probing again is cheap compared to being
                // wrong about a file.
                _log.Debug(error, "Ignoring an unreadable cached probe for {Hash}", hash);
            }
        }

        MediaProbe probe = _prober.Probe(path);
        MediaInfo info = Describe(probe);

        Cache?.PutProbe(ProbeKey(hash), JsonSerializer.Serialize(info, JazzJson.Options));

        return (info, false);
    }

    /// <summary>A picture stream's colour signalling for the project; null for anything else.</summary>
    /// <remarks>
    /// MaxCLL is kept only when it is plausible: some encoders write zero or a placeholder there,
    /// and a MaxCLL of a few nits would make the tone mapper blow the picture out.
    /// </remarks>
    internal static StreamColor? Color(VideoStreamInfo? video) =>
        video is null
            ? null
            : new StreamColor(
                video.Color.Primaries,
                video.Color.Transfer,
                video.Color.Matrix,
                video.Color.IsFullRange,
                video.Hdr?.MaxLuminanceNits ?? 0,
                video.Hdr is { MaxContentLightLevel: >= 100 and <= 10000 } hdr ? hdr.MaxContentLightLevel : 0);

    /// <summary>
    /// Flattens a prober result into the summary the project stores.
    /// </summary>
    /// <remarks>
    /// The project model cannot reference the media layer, so this is the boundary: everything
    /// FFmpeg-shaped stops here and everything above works from plain records.
    /// </remarks>
    public static MediaInfo Describe(MediaProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var streams = ImmutableArray.CreateBuilder<MediaStream>(probe.Streams.Count);

        foreach (StreamInfo stream in probe.Streams)
        {
            streams.Add(new MediaStream(
                stream.Index,
                stream.Kind switch
                {
                    StreamKind.Video => MediaStreamKind.Video,
                    StreamKind.Audio => MediaStreamKind.Audio,
                    StreamKind.Subtitle => MediaStreamKind.Subtitle,
                    StreamKind.Attachment => MediaStreamKind.Attachment,
                    _ => MediaStreamKind.Data,
                },
                stream.CodecName,
                stream.Duration,
                stream.Language,
                stream.Title,
                stream.Video?.Width ?? 0,
                stream.Video?.Height ?? 0,
                stream.Video?.FrameRate,
                stream.Video?.BitDepth ?? 0,
                stream.Video?.IsInterlaced ?? false,
                stream.Video?.FrameRateMode == FrameRateMode.Variable,
                stream.Video?.Color.IsHdr ?? false,
                stream.Video?.HasAlpha ?? false,
                stream.Audio?.SampleRate ?? 0,
                stream.Audio?.Channels ?? 0,
                stream.Audio?.ChannelLayout ?? string.Empty,
                Color(stream.Video)));
        }

        return new MediaInfo(
            probe.FormatName,
            probe.Duration,
            probe.SizeBytes,
            probe.BitRate,
            new EquatableArray<MediaStream>(streams.ToImmutable()),
            DateTimeOffset.UtcNow,
            new EquatableArray<MediaChapter>([.. probe.Chapters.Select(chapter => new MediaChapter(chapter.Start, chapter.End, chapter.Title ?? string.Empty))]));
    }

    /// <summary>
    /// What the user should be told before they start cutting with this file.
    /// </summary>
    /// <remarks>
    /// Each of these is something that will otherwise be discovered late: frames that drift,
    /// fields that comb, colours that look wrong on an SDR monitor, or a camera file that turns
    /// out to have no sound. Saying so at import costs nothing; finding out at export costs the
    /// evening.
    /// </remarks>
    public static ImmutableArray<ImportWarning> Warnings(MediaItem item, MediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(info);

        var warnings = ImmutableArray.CreateBuilder<ImportWarning>();

        if (info.IsVariableFrameRate)
        {
            warnings.Add(new ImportWarning(
                "variable-frame-rate",
                item.ShouldConformFrameRate
                    ? "The frames do not sit on a fixed grid, so they will be conformed to the project rate on decode."
                    : "The frames do not sit on a fixed grid, and conforming is off, so cuts may land between frames."));
        }

        if (info.IsInterlaced)
        {
            warnings.Add(new ImportWarning(
                "interlaced",
                item.ShouldDeinterlace
                    ? "The picture is interlaced and will be deinterlaced on decode."
                    : "The picture is interlaced and deinterlacing is off, so it will comb on motion."));
        }

        if (info.IsHdr)
        {
            warnings.Add(new ImportWarning(
                "hdr",
                "The picture carries an HDR transfer function. Check the sequence colour space before grading."));
        }

        if (info.IsSilent && !item.IsImages)
        {
            warnings.Add(new ImportWarning("no-audio", "There is no sound in this file."));
        }

        if (info.Streams.IsEmpty)
        {
            warnings.Add(new ImportWarning(
                "no-streams",
                "Nothing in this file could be decoded. It may be a format Jazz Hands does not read."));
        }

        return warnings.ToImmutable();
    }

    /// <summary>
    /// Collapses runs of numbered images into one source each, leaving everything else alone.
    /// </summary>
    /// <remarks>
    /// Files are grouped by folder, stem, digit width and extension, and a group only becomes a
    /// sequence when there are at least two of them: a single <c>poster.001.png</c> is a still,
    /// not a sequence of one.
    /// </remarks>
    private static ImmutableArray<ImportSource> Group(List<string> files)
    {
        var sources = ImmutableArray.CreateBuilder<ImportSource>();
        var runs = new Dictionary<string, List<(string Path, int Number)>>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            string extension = Path.GetExtension(file);

            if (!StillExtensions.Contains(extension))
            {
                sources.Add(new ImportSource(file, MediaKind.Movie));
                continue;
            }

            Match match = NumberedFile.Match(Path.GetFileName(file));

            if (!match.Success)
            {
                sources.Add(new ImportSource(file, MediaKind.Still));
                continue;
            }

            string key = string.Join(
                '\u0000',
                Path.GetDirectoryName(file) ?? string.Empty,
                match.Groups["stem"].Value,
                match.Groups["number"].Value.Length.ToString(CultureInfo.InvariantCulture),
                extension);

            if (!runs.TryGetValue(key, out List<(string, int)>? run))
            {
                run = [];
                runs[key] = run;
            }

            run.Add((file, int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture)));
        }

        foreach ((string key, List<(string Path, int Number)> run) in runs)
        {
            run.Sort((left, right) => left.Number.CompareTo(right.Number));

            if (run.Count == 1)
            {
                sources.Add(new ImportSource(run[0].Path, MediaKind.Still));
                continue;
            }

            string[] parts = key.Split('\u0000');
            int padding = int.Parse(parts[2], CultureInfo.InvariantCulture);

            sources.Add(new ImportSource(
                run[0].Path,
                MediaKind.ImageSequence,
                [.. run.Select(member => member.Path)],
                $"{parts[1]}%0{padding}d{parts[3]}",
                parts[1],
                run[0].Number,
                padding));
        }

        return [.. sources.OrderBy(source => source.Path, StringComparer.OrdinalIgnoreCase)];
    }
}

/// <summary>One thing to import: a file, a still, or a run of numbered images.</summary>
/// <param name="Path">The file, or the first file of a run.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Members">Every file of a run, in order. Just the one otherwise.</param>
/// <param name="Pattern">The numbered pattern, for a run.</param>
/// <param name="Stem">The part of the name before the number, for a run.</param>
/// <param name="SequenceStart">The first number in a run.</param>
/// <param name="SequencePadding">How many digits the numbers are padded to.</param>
public sealed record ImportSource(
    string Path,
    MediaKind Kind,
    IReadOnlyList<string> Members = null!,
    string Pattern = "",
    string Stem = "",
    int SequenceStart = 0,
    int SequencePadding = 0)
{
    /// <summary>Every file this source covers.</summary>
    public IReadOnlyList<string> Members { get; init; } = Members ?? [Path];
}

/// <summary>What the user chose in the import dialog, or on the command line.</summary>
/// <param name="Folder">Where it goes in the bin.</param>
/// <param name="Tags">Tags to apply.</param>
/// <param name="Color">A colour label.</param>
/// <param name="Conform">How its picture is fitted.</param>
/// <param name="Deinterlace">Whether to deinterlace.</param>
/// <param name="VfrConform">Whether to remap variable frame timing.</param>
/// <param name="Name">A display name, when the file name will not do.</param>
/// <param name="ImageFrameRate">The rate an image sequence plays at.</param>
/// <param name="StillDuration">How long a still lasts.</param>
public sealed record ImportOptions(
    string Folder = "",
    EquatableArray<string> Tags = default,
    string Color = "",
    ConformPolicy Conform = ConformPolicy.Fit,
    AutoSetting Deinterlace = AutoSetting.Auto,
    AutoSetting VfrConform = AutoSetting.Auto,
    string? Name = null,
    Rational? ImageFrameRate = null,
    Flicks? StillDuration = null);
