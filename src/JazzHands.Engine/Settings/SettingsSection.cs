using System.Text.Json;
using System.Text.Json.Nodes;
using JazzHands.Engine.Caching;
using Serilog;

namespace JazzHands.Engine.Settings;

/// <summary>
/// One section of the editor's settings file, <c>%APPDATA%\JazzHands\settings.json</c>, read and
/// written as a record.
/// </summary>
/// <remarks>
/// The file is one object with a section per concern (<c>cache</c>, <c>control</c>, <c>editor</c>,
/// <c>recent</c>); writing a section leaves the others as they were, so a store never needs to
/// know about the rest. A file or section that cannot be read is logged and treated as the
/// defaults, never fatal: a broken settings file must not stop the editor starting. Writes go to
/// a temporary file that replaces the real one, so a crash mid-write leaves the old settings.
/// </remarks>
/// <typeparam name="T">The section's record; its parameterless defaults are the defaults.</typeparam>
public sealed class SettingsSection<T>
    where T : class, new()
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ILogger _log = Log.ForContext<SettingsSection<T>>();
    private readonly Lock _gate = new();
    private readonly Func<T, T> _tidy;

    /// <summary>A section of a settings file.</summary>
    /// <param name="section">The section's name in the file.</param>
    /// <param name="path">The file, or null for the per-user one.</param>
    /// <param name="tidy">Puts values read back into range, so a hand edit cannot break anything.</param>
    public SettingsSection(string section, string? path = null, Func<T, T>? tidy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        Section = section;
        Path = path ?? CacheSettingsStore.DefaultPath;
        _tidy = tidy ?? (value => value);
        Current = Read();
    }

    /// <summary>Raised after <see cref="Save"/> with what was saved.</summary>
    public event EventHandler<T>? Saved;

    /// <summary>The section's name.</summary>
    public string Section { get; }

    /// <summary>The file.</summary>
    public string Path { get; }

    /// <summary>The settings as last read or written.</summary>
    public T Current { get; private set; }

    /// <summary>Writes the section, keeping the rest of the file.</summary>
    public void Save(T settings)
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

        Saved?.Invoke(this, settings);
    }

    /// <summary>Changes the section with a function of what it holds, and saves it.</summary>
    public T Update(Func<T, T> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        T changed = change(Current);
        Save(changed);
        return changed;
    }

    private T Read()
    {
        try
        {
            if (Load()?[Section] is JsonNode node && node.Deserialize<T>(Json) is { } read)
            {
                return _tidy(read);
            }
        }
        catch (JsonException error)
        {
            _log.Warning(error, "The {Section} settings in {Path} could not be read; using the defaults", Section, Path);
        }

        return new T();
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
