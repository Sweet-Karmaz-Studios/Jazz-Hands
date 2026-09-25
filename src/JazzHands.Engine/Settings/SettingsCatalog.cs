using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using JazzHands.Core.Commands;

namespace JazzHands.Engine.Settings;

/// <summary>A section of the settings file and the record it reads as.</summary>
/// <param name="Section">Its name in the file.</param>
/// <param name="Type">The record; its defaults are the section's defaults.</param>
/// <param name="Description">What it is for, for <c>settings.get</c>.</param>
public sealed record SettingsSectionType(string Section, Type Type, string Description);

/// <summary>
/// Every setting the <c>settings.get</c> and <c>settings.set</c> commands reach, and the file they
/// are in.
/// </summary>
/// <remarks>
/// <para>
/// The sections are whatever the host registered as <see cref="SettingsSectionType"/>s: the engine
/// adds <c>cache</c>, the control server <c>control</c>, the editor <c>editor</c> and
/// <c>recent</c>. A headless <c>jazz</c> reaches the sections it knows; the settings file is the
/// same one.
/// </para>
/// <para>
/// A value is checked by reading the section, with it, back into the record: a setting that does
/// not exist, or a value of the wrong kind, is refused with a coded error before anything is
/// written. <see cref="Changed"/> tells the running editor, whose stores read the section again and
/// apply what they can.
/// </para>
/// </remarks>
public sealed class SettingsCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly Lock Gate = new();

    private readonly Dictionary<string, SettingsSectionType> _sections;

    /// <summary>A catalog over a settings file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="sections">The sections this process knows.</param>
    public SettingsCatalog(string path, IEnumerable<SettingsSectionType> sections)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(sections);
        Path = path;
        _sections = [];
        foreach (SettingsSectionType section in sections)
        {
            _sections[section.Section] = section;
        }
    }

    /// <summary>Raised with a section's name after <see cref="Set"/> wrote to it.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>The settings file.</summary>
    public string Path { get; }

    /// <summary>The sections known, by name.</summary>
    public IReadOnlyCollection<string> Sections => _sections.Keys;

    /// <summary>Every setting, or a section's, with its value and default.</summary>
    public SettingInfo[] List(string? section = null)
    {
        if (section is not null && !_sections.ContainsKey(section))
        {
            throw Unknown(section);
        }

        JsonObject root = Load();
        var settings = new List<SettingInfo>();
        foreach (SettingsSectionType known in _sections.Values.OrderBy(known => known.Section, StringComparer.Ordinal))
        {
            if (section is not null && known.Section != section)
            {
                continue;
            }

            JsonObject defaults = Defaults(known);
            JsonObject values = Effective(known, root[known.Section] as JsonObject);
            foreach (JsonPropertyInfo property in Json.GetTypeInfo(known.Type).Properties)
            {
                settings.Add(new SettingInfo(
                    $"{known.Section}.{property.Name}",
                    Text(values[property.Name]),
                    Text(defaults[property.Name]),
                    Describe(property.PropertyType),
                    known.Description));
            }
        }

        return [.. settings];
    }

    /// <summary>Changes one setting, after checking it.</summary>
    /// <returns>The setting as it now is.</returns>
    public SettingInfo Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        int dot = key.IndexOf('.', StringComparison.Ordinal);
        string sectionName = dot > 0 ? key[..dot] : key;
        if (!_sections.TryGetValue(sectionName, out SettingsSectionType? section))
        {
            throw Unknown(sectionName);
        }

        string name = dot > 0 ? key[(dot + 1)..] : string.Empty;
        JsonPropertyInfo property = Json.GetTypeInfo(section.Type).Properties
            .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new CommandException(
                "unknown-setting",
                $"There is no setting '{key}'. The {section.Section} settings are {string.Join(", ", Json.GetTypeInfo(section.Type).Properties.Select(known => $"{section.Section}.{known.Name}"))}.",
                "key");

        lock (Gate)
        {
            JsonObject root = Load();
            JsonObject values = root[section.Section] is JsonObject existing ? (JsonObject)existing.DeepClone() : [];
            values[property.Name] = Parse(value, property.PropertyType);

            try
            {
                _ = values.Deserialize(section.Type, Json);
            }
            catch (JsonException)
            {
                throw new CommandException(
                    "bad-setting",
                    $"{key} takes {Describe(property.PropertyType)}, and '{value}' is not that.",
                    "value");
            }

            root[section.Section] = values;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            string temporary = Path + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(Json));
            File.Move(temporary, Path, overwrite: true);
        }

        Changed?.Invoke(this, section.Section);
        return List(section.Section).Single(setting => setting.Key == $"{section.Section}.{property.Name}");
    }

    /// <summary>
    /// A value as typed: JSON when it reads as JSON of the right kind, otherwise the text itself,
    /// so <c>D:\cache</c> and <c>youtube-4k</c> need no quotes.
    /// </summary>
    private static JsonNode? Parse(string value, Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string))
        {
            return value == "null" && type == underlying ? null : JsonValue.Create(value);
        }

        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            return JsonValue.Create(value);
        }
    }

    private static JsonObject Defaults(SettingsSectionType section) =>
        (JsonObject)JsonSerializer.SerializeToNode(JsonSerializer.Deserialize("{}", section.Type, Json), section.Type, Json)!;

    /// <summary>The section as the record reads it: the file's values over the defaults.</summary>
    private static JsonObject Effective(SettingsSectionType section, JsonObject? values)
    {
        try
        {
            object? read = (values ?? []).Deserialize(section.Type, Json);
            return (JsonObject)JsonSerializer.SerializeToNode(read, section.Type, Json)!;
        }
        catch (JsonException)
        {
            return Defaults(section);
        }
    }

    private JsonObject Load()
    {
        try
        {
            return File.Exists(Path) && JsonNode.Parse(File.ReadAllText(Path)) is JsonObject root ? root : [];
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private CommandException Unknown(string section) => new(
        "unknown-setting",
        string.Create(CultureInfo.InvariantCulture, $"There is no '{section}' section. The sections are {string.Join(", ", _sections.Keys.Order(StringComparer.Ordinal))}."),
        "key");

    private static string Text(JsonNode? node) => node?.ToJsonString() ?? "null";

    private static string Describe(Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(string) ? "text"
            : underlying == typeof(bool) ? "true or false"
            : underlying == typeof(int) || underlying == typeof(long) ? "a whole number"
            : underlying == typeof(double) || underlying == typeof(float) ? "a number"
            : underlying.IsEnum ? "one of " + string.Join(", ", Enum.GetNames(underlying))
            : typeof(System.Collections.IEnumerable).IsAssignableFrom(underlying) ? "a list"
            : "an object";
    }
}
