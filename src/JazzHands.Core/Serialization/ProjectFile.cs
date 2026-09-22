using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JazzHands.Core.Model;
using JazzHands.Core.Validation;

namespace JazzHands.Core.Serialization;

/// <summary>What opening a .jazz file produced.</summary>
/// <param name="Project">The project.</param>
/// <param name="Path">The file it came from, as a full path.</param>
/// <param name="Unknown">Members this build did not recognise, to be written back on save.</param>
/// <param name="Issues">Everything worth telling the user, schema and semantic together.</param>
/// <param name="Migrated">The migrations that ran, oldest first.</param>
public sealed record ProjectLoad(
    Project Project,
    string Path,
    UnknownFields Unknown,
    ImmutableArray<ValidationIssue> Issues,
    ImmutableArray<IMigration> Migrated)
{
    /// <summary>True when nothing found is bad enough to refuse the file.</summary>
    public bool IsLoadable => Validator.IsLoadable(Issues);
}

/// <summary>Raised when a file cannot be opened as a project at all.</summary>
public sealed class ProjectFileException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ProjectFileException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an underlying cause.</summary>
    public ProjectFileException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Creates the exception with the schema problems that explain it.</summary>
    public ProjectFileException(string message, ImmutableArray<ValidationIssue> issues, Exception inner)
        : base(message, inner) =>
        Issues = issues;

    /// <summary>Creates the exception with no message. Prefer the other constructors.</summary>
    public ProjectFileException()
    {
    }

    /// <summary>
    /// What the schema found, when the file was well-formed JSON but not a project.
    /// </summary>
    /// <remarks>
    /// Empty for the failures that have nothing to do with the schema, such as a file that is not
    /// there. When it is not empty it is the useful part of the message: one line per member,
    /// each with a pointer a person or an editor can jump to.
    /// </remarks>
    public ImmutableArray<ValidationIssue> Issues { get; } = [];
}

/// <summary>
/// Reads and writes .jazz files.
/// </summary>
/// <remarks>
/// The layer above <see cref="JazzJson"/>: migrations, unknown members, media paths, validation
/// and, above all, never losing the file that is already on disk. Every write goes to a temporary
/// file beside the target and is then swapped in, so a crash, a full disk or a pulled power cable
/// leaves either the old project or the new one and never half of either.
/// </remarks>
public static class ProjectFile
{
    /// <summary>Opens a project file.</summary>
    /// <param name="path">The .jazz file.</param>
    /// <param name="checkSchema">
    /// Run schema validation even when the file loads. Off by default because it costs more than
    /// everything else on this path put together; see <see cref="Parse"/>.
    /// </param>
    /// <exception cref="ProjectFileException">The file is missing, malformed, or from a newer build.</exception>
    public static ProjectLoad Load(string path, bool checkSchema = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new ProjectFileException($"There is no project at '{full}'.");
        }

        string text;
        try
        {
            text = File.ReadAllText(full, Encoding.UTF8);
        }
        catch (IOException error)
        {
            throw new ProjectFileException($"'{full}' could not be read: {error.Message}", error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new ProjectFileException($"'{full}' could not be read: {error.Message}", error);
        }

        return Parse(text, full, checkSchema);
    }

    /// <summary>
    /// Reads project text that is already in hand, for the clipboard, tests, and the recovery
    /// copy.
    /// </summary>
    /// <param name="text">The document.</param>
    /// <param name="path">The path it should be treated as coming from, for relative media paths.</param>
    /// <param name="checkSchema">Run schema validation even when the file loads.</param>
    /// <remarks>
    /// Schema validation does not run on a file that opens. On a five hundred clip project it
    /// takes about 440 ms against about 58 ms for everything else, which would put opening a
    /// project four times over its budget to tell the user nothing. It runs in the two cases
    /// where it pays for itself: when deserialization fails, so the report names the member and
    /// the path rather than being an exception from inside System.Text.Json, and when someone
    /// asks for it with <c>jazz validate</c>, where a fifth of a second is nothing.
    /// </remarks>
    public static ProjectLoad Parse(string text, string path, bool checkSchema = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException error)
        {
            throw new ProjectFileException(
                $"'{path}' is not valid JSON: {error.Message} (line {error.LineNumber + 1}, position {error.BytePositionInLine})",
                error);
        }

        if (node is not JsonObject document)
        {
            throw new ProjectFileException($"'{path}' holds {DescribeRoot(node)} rather than a project object.");
        }

        int version = Migrations.VersionOf(document);
        if (!Migrations.Upgrade(document, out ImmutableArray<IMigration> migrated))
        {
            throw new ProjectFileException(
                version > Project.CurrentSchemaVersion
                    ? $"'{path}' is schema version {version}, and this build writes version {Project.CurrentSchemaVersion}. Open it with a newer Jazz Hands."
                    : $"'{path}' is schema version {version}, which this build cannot upgrade. The oldest it opens is {Migrations.OldestSupported}.");
        }

        ImmutableArray<ValidationIssue> schemaIssues = checkSchema ? Validator.Schema(document) : [];

        Project project;
        try
        {
            project = ProjectNormalizer.Normalize(
                document.Deserialize<Project>(JazzJson.Options)
                    ?? throw new ProjectFileException($"'{path}' holds a null project."));
        }
        catch (JsonException error)
        {
            // Now it is worth the time: the schema turns "the JSON value could not be converted"
            // into "/settings/width: wrong-type", which is the difference between a user fixing
            // their file and giving up on it.
            ImmutableArray<ValidationIssue> found = checkSchema ? schemaIssues : Validator.Schema(document);

            string detail = found.IsEmpty
                ? error.Message
                : string.Join("; ", found.Select(issue => $"{issue.Path}: {issue.Message}"));

            throw new ProjectFileException($"'{path}' does not match the project format: {detail}", found, error);
        }

        // Anything in the file that this build did not consume is kept so that saving does not
        // destroy it. Comparing against what this build would write is what finds it.
        JsonNode? written = JsonSerializer.SerializeToNode(project, JazzJson.Options);
        UnknownFields unknown = UnknownFields.Capture(document, written);

        ImmutableArray<ValidationIssue> issues =
        [
            .. schemaIssues,
            .. unknown.Issues(),
            .. Validator.Semantic(project),
            .. MediaPathIssues(project),
        ];

        return new ProjectLoad(project, path, unknown, issues, migrated);
    }

    /// <summary>
    /// Writes a project, atomically, converting media paths to the form the file stores.
    /// </summary>
    /// <param name="project">The project to write.</param>
    /// <param name="path">Where to write it.</param>
    /// <param name="unknown">Members kept from the file this project was read from.</param>
    /// <returns>The project as written, which may differ from the one passed in by its media paths.</returns>
    public static Project Save(string path, Project project, UnknownFields? unknown = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(project);

        string full = Path.GetFullPath(path);
        Project stored = WithStoredPaths(full, project);

        WriteAtomic(full, Render(stored, unknown));
        return stored;
    }

    /// <summary>The exact bytes <see cref="Save"/> would write, as text.</summary>
    public static string Render(Project project, UnknownFields? unknown = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (unknown is null || unknown.IsEmpty)
        {
            return JazzJson.Serialize(project);
        }

        JsonNode? node = JsonSerializer.SerializeToNode(project, JazzJson.Options);
        unknown.Apply(node);

        return node!.ToJsonString(JazzJson.Options) + "\n";
    }

    /// <summary>
    /// Writes text to a file so that the old contents survive a failure.
    /// </summary>
    /// <remarks>
    /// The sequence is: write a temporary file in the same folder, flush it to the device, then
    /// swap it over the target. Same folder matters, because a move within a volume is a rename
    /// and a move across volumes is a copy, and only the rename is atomic. The flush matters
    /// because a rename that reaches the disk before the data does leaves an empty project.
    /// </remarks>
    public static void WriteAtomic(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        string full = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(full) ?? ".";
        Directory.CreateDirectory(folder);

        string temporary = Path.Combine(folder, $".{Path.GetFileName(full)}.{Id.New()}.tmp");

        try
        {
            // UTF-8 with no byte order mark. A BOM makes the file a nuisance for every other
            // tool that reads it, and JSON does not want one.
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(full))
            {
                // Replace keeps the target's attributes and access control, which Move does not.
                File.Replace(temporary, full, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, full);
            }
        }
        catch (IOException) when (File.Exists(temporary))
        {
            // Replace refuses across volumes and on some network shares. A plain overwrite is
            // less safe but better than not saving at all, and the temporary file still holds
            // the new contents until the move completes.
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Rewrites media paths into the form the file stores, relative where it can be.</summary>
    public static Project WithStoredPaths(string projectPath, Project project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(project);

        if (project.Media.IsEmpty)
        {
            return project;
        }

        var media = ImmutableArray.CreateBuilder<MediaItem>(project.Media.Length);
        bool changed = false;

        foreach (MediaItem item in project.Media)
        {
            // An item with no path at all is broken, and the validator says so. Saving it back
            // exactly as it came in is better than refusing to save the rest of the project.
            if (string.IsNullOrWhiteSpace(item.RelativePath))
            {
                media.Add(item);
                continue;
            }

            string stored = ProjectPaths.Store(projectPath, item.RelativePath);
            changed |= !string.Equals(stored, item.RelativePath, StringComparison.Ordinal);
            media.Add(item with { RelativePath = stored });
        }

        return changed ? project with { Media = new EquatableArray<MediaItem>(media.ToImmutable()) } : project;
    }

    private static ImmutableArray<ValidationIssue> MediaPathIssues(Project project)
    {
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();

        for (int index = 0; index < project.Media.Length; index++)
        {
            MediaItem item = project.Media[index];

            // A hand-edited file can leave the path out entirely, which deserializes to null. It
            // has to be reported rather than thrown out of the loader.
            if (string.IsNullOrWhiteSpace(item.RelativePath))
            {
                issues.Add(new ValidationIssue(
                    Severity.Error,
                    "media-without-path",
                    $"/media/{index}/relativePath",
                    $"The media item '{item.Id}' has no path, so nothing can play it."));
                continue;
            }

            if (ProjectPaths.IsMachineSpecific(item.RelativePath))
            {
                issues.Add(new ValidationIssue(
                    Severity.Warning,
                    "absolute-media-path",
                    $"/media/{index}/relativePath",
                    $"'{item.RelativePath}' is an absolute path, so this project will only open on a machine that has that drive. Keep the media beside the project to make it relative."));
            }
        }

        return issues.ToImmutable();
    }

    private static string DescribeRoot(JsonNode? node) => node switch
    {
        null => "null",
        JsonArray => "an array",
        JsonValue => "a single value",
        _ => "something unexpected",
    };
}
