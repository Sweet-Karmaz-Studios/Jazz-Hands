using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace JazzHands.Engine.Caching;

/// <summary>Where the cache lives and how big it may get.</summary>
/// <param name="Location">The cache folder, or null for the default under local application data.</param>
/// <param name="CapBytes">The most thumbnails and waveforms may take; 0 for no limit.</param>
public sealed record CacheSettings(string? Location = null, long CapBytes = CacheSettings.DefaultCapBytes)
{
    /// <summary>20 GB, as the caching skill sets.</summary>
    public const long DefaultCapBytes = 20L * 1024 * 1024 * 1024;
}

/// <summary>
/// Reads and writes the editor's settings file, the cache's part of it.
/// </summary>
/// <remarks>
/// <c>%APPDATA%\JazzHands\settings.json</c>, one object with a section per concern. Only the
/// <c>cache</c> section is this class's; writing it leaves every other section as it was, so a
/// later phase adding its own does not have to know about this one. A file that cannot be read
/// is logged and treated as defaults, never fatal: a broken settings file must not stop the editor.
///
/// The path is a constructor argument so tests never touch the real one.
/// </remarks>
public sealed class CacheSettingsStore
{
    private const string Section = "cache";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ILogger _log = Log.ForContext<CacheSettingsStore>();
    private readonly Lock _gate = new();

    /// <summary>A store over a settings file.</summary>
    /// <param name="path">The file, or null for the per-user one.</param>
    public CacheSettingsStore(string? path = null)
    {
        Path = path ?? DefaultPath;
        Current = Read();
    }

    /// <summary>The per-user settings file.</summary>
    public static string DefaultPath => System.IO.Path.Combine(
        JazzHands.Core.JazzFolders.Roaming,
        "settings.json");

    /// <summary>The file.</summary>
    public string Path { get; }

    /// <summary>The settings as last read or written.</summary>
    public CacheSettings Current { get; private set; }

    /// <summary>Writes new settings, keeping the rest of the file.</summary>
    public void Save(CacheSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            JsonObject root = Load() ?? [];
            root[Section] = JsonSerializer.SerializeToNode(settings, Json);

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);

            string temporary = Path + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(Json));
            File.Move(temporary, Path, overwrite: true);

            Current = settings;
        }
    }

    private CacheSettings Read()
    {
        try
        {
            if (Load()?[Section] is JsonNode node && node.Deserialize<CacheSettings>(Json) is { } settings)
            {
                return settings with { CapBytes = Math.Max(0, settings.CapBytes) };
            }
        }
        catch (JsonException error)
        {
            _log.Warning(error, "The cache settings in {Path} could not be read; using the defaults", Path);
        }

        return new CacheSettings();
    }

    private JsonObject? Load()
    {
        try
        {
            return File.Exists(Path) ? JsonNode.Parse(File.ReadAllText(Path)) as JsonObject : null;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.Warning(error, "The settings file {Path} could not be read", Path);
            return null;
        }
    }
}
