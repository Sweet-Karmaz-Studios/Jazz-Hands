using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using JazzHands.Core;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Engine.Export;
using JazzHands.Media.Import;
using JazzHands.Media.Interop;
using JazzHands.Render.Scene;
using Serilog;

namespace JazzHands.Engine.Library;

/// <summary>What a consolidate did.</summary>
/// <param name="ProjectPath">The project it wrote.</param>
/// <param name="Files">Media files written.</param>
/// <param name="Bytes">Their size together.</param>
/// <param name="Trimmed">Media items cut down to what is used.</param>
/// <param name="Left">Media items left out because no clip uses them.</param>
public sealed record ConsolidateResult(string ProjectPath, int Files, long Bytes, int Trimmed, int Left);

/// <summary>
/// Gathers a project and the media its clips use into one folder, optionally keeping only the used
/// parts; and zips that folder for <c>project.archive</c>.
/// </summary>
/// <remarks>
/// <para>
/// The gathered project is the open one with its media paths pointing into <c>media\</c>, and
/// without media no clip uses. Whole files are copied (or moved) with their dates, so their hashes,
/// thumbnails and proxies carry over. A trimmed file is a smart cut of one used stretch, aligned
/// outwards to the source's frames; the clips that play from it point at it, their source times
/// moved back by where the stretch starts, so every frame lands where it did. A recording used for
/// most of its length is copied whole instead, as is anything that is not a moving picture or that
/// smart cut cannot match.
/// </para>
/// <para>
/// Nothing is written when media is missing: a gathered copy with holes in it would be worse than
/// none. Relink first.
/// </para>
/// </remarks>
public static class Consolidator
{
    /// <summary>How much of a file must be used for trimming it not to be worth a cut.</summary>
    public const double WholeWhenUsed = 0.9;

    /// <summary>The media folder inside a gathered project.</summary>
    public const string MediaFolder = "media";

    /// <summary>The folder inside a gathered project that 3D models go in, each in a folder of its own with the files it reads.</summary>
    public const string ModelsFolder = "models";

    /// <summary>Gathers the project into a folder.</summary>
    /// <param name="project">The project as it is now.</param>
    /// <param name="projectPath">Where it lives, or empty for one never saved.</param>
    /// <param name="command">Where to and how.</param>
    /// <param name="environment">How to encode what smart cut encodes; software only when it forces WARP.</param>
    /// <param name="cancellationToken">Stops between files.</param>
    public static ConsolidateResult Run(Project project, string projectPath, ConsolidateProjectCommand command, ExportEnvironment? environment = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);

        if (command.Move && command.Trim)
        {
            throw new CommandException("move-and-trim", "Moving keeps whole files; trimming makes new ones. Choose one.", "move");
        }

        string folder = Resolve(projectPath, command.To);
        string name = project.Name.Length > 0 ? Safe(project.Name) : "project";
        string target = Path.Combine(folder, name + ".jazz");
        if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.jazz").Any() && !command.Overwrite)
        {
            throw new CommandException("folder-has-project", $"'{folder}' already has a project in it. Pass --overwrite, or choose an empty folder.", "to");
        }

        if (projectPath.Length > 0 && string.Equals(Path.GetFullPath(projectPath), target, StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandException("same-folder", "That is where the project already is. Gather it somewhere else.", "to");
        }

        MissingMediaInfo[] missing = MediaLibrary.FindMissing(project, projectPath, [], null);
        if (missing.Length > 0)
        {
            throw new CommandException(
                "media-missing",
                $"{Words.Count(missing.Length, "media file")} {(missing.Length == 1 ? "is" : "are")} missing: {string.Join(", ", missing.Select(item => item.Name))}. Relink them first (media.relink --auto).");
        }

        // A 3D model is a file a parameter names, not media (Phase 49a): it must be there too.
        ParamReference[] fileReferences = [.. ParamReferences.All(project).Where(reference => reference.Kind == ParamReferenceKind.File)];
        string[] modelFiles = [.. fileReferences.Select(reference => ProjectPaths.Resolve(projectPath, reference.Value)).Distinct(StringComparer.OrdinalIgnoreCase)];
        string[] lost = [.. modelFiles.Where(path => !File.Exists(path))];
        if (lost.Length > 0)
        {
            throw new CommandException(
                "files-missing",
                $"{Words.Count(lost.Length, "model file")} {(lost.Length == 1 ? "is" : "are")} missing: {string.Join(", ", lost.Select(Path.GetFileName))}. Put {(lost.Length == 1 ? "it" : "them")} back, or point the model at another file, first.");
        }

        // Media a comp graph's node or a light names is gathered whole: its id must keep meaning
        // the whole file, wherever in it the node plays.
        HashSet<string> referenced = ParamReferences.MediaIds(project);

        Flicks handles = command.Handles ?? Flicks.FromSeconds(1);
        if (handles < Flicks.Zero)
        {
            throw new CommandException("bad-handles", "Handles cannot be negative.", "handles");
        }

        Dictionary<string, ImmutableArray<TimeRange>> used = MediaLibrary.UsedRanges(project, command.Trim ? handles : Flicks.Zero);
        string media = Path.Combine(folder, MediaFolder);

        // What was there before, so a gather that is cancelled or fails part way can put
        // everything back: moved files returned, copies and pieces removed (Phase 33).
        bool folderExisted = Directory.Exists(folder);
        bool mediaExisted = Directory.Exists(media);
        bool modelsExisted = Directory.Exists(Path.Combine(folder, ModelsFolder));
        HashSet<string> before = mediaExisted
            ? new HashSet<string>(Directory.EnumerateFileSystemEntries(media, "*", SearchOption.AllDirectories), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moved = new List<(string From, string To)>();
        Directory.CreateDirectory(media);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pieces = new Dictionary<string, List<(TimeRange Range, MediaItem Item)>>(StringComparer.Ordinal);
        var kept = new List<MediaItem>();
        int files = 0;
        long bytes = 0;
        int trimmed = 0;

        try
        {
            foreach (MediaItem item in project.Media)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!used.TryGetValue(item.Id, out ImmutableArray<TimeRange> ranges) && !referenced.Contains(item.Id))
                {
                    continue;
                }

                string source = MediaLibrary.FullPath(projectPath, item);
                if (command.Trim && !referenced.Contains(item.Id) && Trimmable(item, ranges) is { } aligned
                    && TryTrim(project, projectPath, item, source, aligned, media, names, environment, cancellationToken) is { } cut)
                {
                    pieces[item.Id] = cut;
                    kept.AddRange(cut.Select(piece => piece.Item));
                    files += cut.Count;
                    bytes += cut.Sum(piece => new FileInfo(MediaLibrary.FullPath(target, piece.Item)).Length);
                    trimmed++;
                    continue;
                }

                string written = Whole(item, source, media, names, command.Move, moved);
                kept.Add(item with { RelativePath = written });
                files++;
                bytes += item.Kind == MediaKind.ImageSequence ? 0 : new FileInfo(written).Length;
            }

            Dictionary<string, string> models = CopyModels(modelFiles, folder, ref files, ref bytes, cancellationToken);

            // Every clip points where its picture went; media nothing uses is left behind.
            Project gathered = project with
            {
                Media = [.. kept.Select(item => item with { RelativePath = ProjectPaths.Store(target, item.RelativePath) })],
                Sequences = [.. project.Sequences.Select(sequence => sequence with
                {
                    Tracks = [.. sequence.Tracks.Select(track => track with { Clips = [.. track.Clips.Select(clip => Repoint(clip, pieces))] })],
                })],
            };

            gathered = ParamReferences.Replace(gathered, reference =>
                reference.Kind == ParamReferenceKind.File && models.TryGetValue(ProjectPaths.Resolve(projectPath, reference.Value), out string? copy)
                    ? ProjectPaths.Store(target, copy)
                    : null);

            CopyFonts(projectPath, folder);
            ProjectFile.Save(target, gathered);
        }
        catch (Exception)
        {
            if (!modelsExisted && Directory.Exists(Path.Combine(folder, ModelsFolder)))
            {
                Directory.Delete(Path.Combine(folder, ModelsFolder), recursive: true);
            }

            Rollback(folder, media, folderExisted, mediaExisted, before, moved);
            throw;
        }

        Log.ForContext(typeof(Consolidator)).Information(
            "Gathered {Project} into {Folder}: {Files} file(s), {Bytes} bytes, {Trimmed} trimmed",
            project.Name,
            folder,
            files,
            bytes,
            trimmed);

        return new ConsolidateResult(target, files, bytes, trimmed, project.Media.Count(item => !used.ContainsKey(item.Id) && !referenced.Contains(item.Id)));
    }

    /// <summary>Gathers the project into a folder beside the zip, zips it, and removes the folder.</summary>
    public static ConsolidateResult Archive(Project project, string projectPath, ArchiveProjectCommand command, ExportEnvironment? environment = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        string zip = Resolve(projectPath, command.To);
        if (!zip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            zip += ".zip";
        }

        string staging = Path.Combine(Path.GetDirectoryName(zip)!, "." + Path.GetFileNameWithoutExtension(zip) + "-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            ConsolidateResult result = Run(project, projectPath, new ConsolidateProjectCommand(staging, command.Trim, command.Handles), environment, cancellationToken);
            string temporary = zip + ".tmp";
            File.Delete(temporary);

            // Video does not compress; storing is as small and far faster.
            ZipFile.CreateFromDirectory(staging, temporary, CompressionLevel.NoCompression, includeBaseDirectory: false);
            File.Move(temporary, zip, overwrite: true);
            return result with { ProjectPath = zip, Bytes = new FileInfo(zip).Length };
        }
        finally
        {
            // A zip cut short is not left beside where the whole one would have gone.
            if (File.Exists(zip + ".tmp"))
            {
                File.Delete(zip + ".tmp");
            }

            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>The used stretches aligned outwards to the source's frames, or null when the whole file is the better copy.</summary>
    private static ImmutableArray<TimeRange>? Trimmable(MediaItem item, ImmutableArray<TimeRange> ranges)
    {
        if (item.Kind != MediaKind.Movie || item.Info?.VideoStreams.FirstOrDefault() is not { FrameRate: { } rate } || item.Duration <= Flicks.Zero)
        {
            return null;
        }

        var aligned = new List<TimeRange>();
        foreach (TimeRange range in ranges)
        {
            long first = range.Start.ToFrames(rate, RoundingMode.Floor);
            long last = range.End.ToFrames(rate, RoundingMode.Ceiling);
            Flicks start = Flicks.FromFrames(first, rate);
            Flicks end = Flicks.Min(Flicks.FromFrames(last, rate), item.Duration);
            if (aligned.Count > 0 && start <= aligned[^1].End)
            {
                aligned[^1] = TimeRange.FromBounds(aligned[^1].Start, Flicks.Max(aligned[^1].End, end));
            }
            else if (end > start)
            {
                aligned.Add(TimeRange.FromBounds(start, end));
            }
        }

        Flicks kept = aligned.Aggregate(Flicks.Zero, (sum, range) => sum + range.Duration);
        return kept.Value >= item.Duration.Value * WholeWhenUsed ? null : [.. aligned];
    }

    /// <summary>Each stretch smart cut into a file of its own, or null when a cut fails and the file goes whole.</summary>
    private static List<(TimeRange Range, MediaItem Item)>? TryTrim(
        Project project,
        string projectPath,
        MediaItem item,
        string source,
        ImmutableArray<TimeRange> ranges,
        string media,
        HashSet<string> names,
        ExportEnvironment? environment,
        CancellationToken cancellationToken)
    {
        var pieces = new List<(TimeRange, MediaItem)>();
        var written = new List<string>();
        try
        {
            MediaItem absolute = item with { RelativePath = source };
            Project single = Project.CreateNew(item.Name) with { Media = EquatableArray.Create(absolute) };
            int next = 0;
            Sequence sequence = QuickTrimOps.Create("consolidate", item.Name, absolute, single.Settings, () => string.Create(CultureInfo.InvariantCulture, $"c{next++}"));
            single = single with { Sequences = EquatableArray.Create(sequence), ActiveSequenceId = sequence.Id };

            var importer = new MediaImporter();
            for (int index = 0; index < ranges.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeRange range = ranges[index];
                string output = Unique(Path.Combine(media, $"{Path.GetFileNameWithoutExtension(source)}_{index + 1}{Path.GetExtension(source)}"), names);

                ExportPlan plan = ExportPlanner.Plan(
                    single,
                    string.Empty,
                    new ExportRequest(output, Mode: ExportMode.Smart, Range: range),
                    environment?.Keyframes ?? new KeyframeLookup(null),
                    cancellationToken);
                if (environment?.ForceWarp == true && plan.Smart is { } smart)
                {
                    // A software run (tests, WARP) keeps off the GPU's encoders too.
                    plan = plan with { Smart = smart with { Encoders = [.. smart.Encoders.Where(encoder => !encoder.Contains("nvenc", StringComparison.Ordinal))] } };
                }

                ExportResult result = Exporter.Run(plan, single, string.Empty, environment, cancellationToken: cancellationToken);
                written.Add(result.Path);

                MediaItem piece = importer.Import(new ImportSource(result.Path, MediaKind.Movie), new ImportOptions(item.Folder, item.Tags, item.Color, item.Conform, item.Deinterlace, item.VfrConform)).Item;
                pieces.Add((range, item with
                {
                    Id = index == 0 ? item.Id : Id.New(),
                    Name = ranges.Length == 1 ? item.Name : string.Create(CultureInfo.InvariantCulture, $"{item.Name} ({index + 1} of {ranges.Length})"),
                    RelativePath = result.Path,
                    Hash = piece.Hash,
                    Info = piece.Info,
                    Duration = piece.Duration,
                    ProxyPath = null,
                }));
            }

            return pieces;
        }
        catch (Exception error) when (error is CommandException or FfmpegException or IOException or InvalidOperationException)
        {
            Log.ForContext(typeof(Consolidator)).Warning(error, "{Name} could not be trimmed; it is copied whole", item.Name);
            foreach (string path in written)
            {
                File.Delete(path);
                names.Remove(Path.GetFileName(path));
            }

            return null;
        }
    }

    /// <summary>A clip pointed at the piece of its media that holds it, its source time moved to match.</summary>
    private static Clip Repoint(Clip clip, Dictionary<string, List<(TimeRange Range, MediaItem Item)>> pieces)
    {
        if (clip.MediaId is not { } id || !pieces.TryGetValue(id, out List<(TimeRange Range, MediaItem Item)>? cut))
        {
            return clip;
        }

        (TimeRange range, MediaItem piece) = cut.FirstOrDefault(candidate => candidate.Range.Start <= clip.SourceRange.Start && clip.SourceRange.End <= candidate.Range.End);
        return piece is null ? clip : clip with { MediaId = piece.Id, SourceIn = clip.SourceIn - range.Start };
    }

    /// <summary>A whole file copied (or moved) into the media folder, with its dates; its new full path.</summary>
    /// <summary>Puts back what a gather that did not finish had done: moved files returned, everything it made removed.</summary>
    private static void Rollback(string folder, string media, bool folderExisted, bool mediaExisted, HashSet<string> before, List<(string From, string To)> moved)
    {
        Serilog.ILogger log = Log.ForContext(typeof(Consolidator));
        for (int index = moved.Count - 1; index >= 0; index--)
        {
            (string from, string to) = moved[index];
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(from)!);
                File.Move(to, from);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log.Error(error, "Could not move {File} back to {Place} after the gather stopped", to, from);
            }
        }

        if (!Directory.Exists(media))
        {
            return;
        }

        string[] made = [.. Directory.EnumerateFileSystemEntries(media, "*", SearchOption.AllDirectories).Where(entry => !before.Contains(entry))];
        foreach (string file in made.Where(File.Exists))
        {
            TryRemove(() => File.Delete(file));
        }

        foreach (string directory in made.Where(Directory.Exists).OrderByDescending(entry => entry.Length))
        {
            TryRemove(() => Directory.Delete(directory));
        }

        if (!mediaExisted)
        {
            TryRemove(() => Directory.Delete(media));
        }

        if (!folderExisted)
        {
            TryRemove(() => Directory.Delete(folder, recursive: false));
        }

        void TryRemove(Action remove)
        {
            try
            {
                remove();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log.Warning(error, "Could not tidy up after the gather stopped");
            }
        }
    }

    private static string Whole(MediaItem item, string source, string media, HashSet<string> names, bool move, List<(string From, string To)> moved)
    {
        if (item.Kind == MediaKind.ImageSequence)
        {
            string from = Path.GetDirectoryName(source)!;
            string to = Unique(Path.Combine(media, Path.GetFileName(from)), names);
            Directory.CreateDirectory(to);
            foreach (string file in Directory.EnumerateFiles(from))
            {
                string destination = Path.Combine(to, Path.GetFileName(file));
                if (move)
                {
                    File.Move(file, destination);
                    moved.Add((file, destination));
                }
                else
                {
                    File.Copy(file, destination);
                }
            }

            return Path.Combine(to, Path.GetFileName(source));
        }

        string target = Unique(Path.Combine(media, Path.GetFileName(source)), names);
        if (move)
        {
            File.Move(source, target);
            moved.Add((source, target));
        }
        else
        {
            File.Copy(source, target);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        }

        return target;
    }

    /// <summary>
    /// Each model copied into a folder of its own under <see cref="ModelsFolder"/>, with the
    /// buffers and pictures a .gltf names beside it, in the same places relative to it so the file
    /// reads unchanged; a map from each model's full path to its copy. Models are always copied,
    /// even when the media is moved: one model is often shared by several projects.
    /// </summary>
    private static Dictionary<string, string> CopyModels(string[] sources, string folder, ref int files, ref long bytes, CancellationToken cancellationToken)
    {
        var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string models = Path.Combine(folder, ModelsFolder);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string home = Unique(Path.Combine(models, Path.GetFileNameWithoutExtension(source)), names);
            string from = Path.GetDirectoryName(source)!;
            var written = new List<(string From, string To)> { (source, Path.Combine(home, Path.GetFileName(source))) };
            foreach (string side in Gltf.SideFiles(source))
            {
                string to = Path.GetFullPath(Path.Combine(home, side));
                if (!to.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CommandException(
                        "model-reaches-out",
                        $"'{Path.GetFileName(source)}' reads '{side}', outside its own folder, so a copy of it could not find it. Move the file beside the model (and change its path in the .gltf) first.");
                }

                string sideFrom = Path.GetFullPath(Path.Combine(from, side));
                if (File.Exists(sideFrom))
                {
                    written.Add((sideFrom, to));
                }
            }

            foreach ((string copyFrom, string copyTo) in written)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copyTo)!);
                File.Copy(copyFrom, copyTo, overwrite: true);
                File.SetLastWriteTimeUtc(copyTo, File.GetLastWriteTimeUtc(copyFrom));
                files++;
                bytes += new FileInfo(copyTo).Length;
            }

            copies[source] = written[0].To;
        }

        return copies;
    }

    private static void CopyFonts(string projectPath, string folder)
    {
        if (projectPath.Length == 0 || Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, "fonts") is not { } fonts || !Directory.Exists(fonts))
        {
            return;
        }

        string to = Path.Combine(folder, "fonts");
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(fonts))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
    }

    /// <summary>A name in the media folder not already taken, "name (2).ext" when it is.</summary>
    private static string Unique(string path, HashSet<string> names)
    {
        string folder = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        string candidate = Path.GetFileName(path);
        for (int number = 2; names.Contains(candidate) || File.Exists(Path.Combine(folder, candidate)) || Directory.Exists(Path.Combine(folder, candidate)); number++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{stem} ({number}){extension}");
        }

        names.Add(candidate);
        return Path.Combine(folder, candidate);
    }

    private static string Resolve(string projectPath, string path) =>
        Path.IsPathRooted(path) || projectPath.Length == 0
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, path));

    private static string Safe(string name) =>
        string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}
