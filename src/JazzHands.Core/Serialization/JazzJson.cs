using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization.Converters;

namespace JazzHands.Core.Serialization;

/// <summary>
/// The one set of System.Text.Json options that reads and writes .jazz files.
/// </summary>
/// <remarks>
/// Everything about the file format that is not a converter lives here, and all of it exists to
/// make the output deterministic: the same project must produce the same bytes on every machine
/// and every run, or git diffs are noise and the round-trip test means nothing.
///
/// The model records are serialized directly rather than through a parallel set of file-shaped
/// types. One source of truth means a new field on <see cref="Clip"/> reaches the file and the
/// schema without a mapping step to forget, which matters over the remaining phases. The cost is
/// that file compatibility is the model's problem, and that is what <see cref="Migrations"/> is
/// for: old documents are reshaped as JSON before anything is deserialized.
/// </remarks>
public static class JazzJson
{
    private static readonly Lazy<JsonSerializerOptions> LazyOptions = new(BuildOptions);

    /// <summary>The options for reading and writing project files.</summary>
    public static JsonSerializerOptions Options => LazyOptions.Value;

    /// <summary>
    /// Reads a project from JSON text, without migrations or path handling.
    /// </summary>
    /// <remarks>
    /// <see cref="ProjectFile"/> is what opens a file. This is the layer under it, used by tests,
    /// by the clipboard, and by anything that already holds the document.
    /// </remarks>
    public static Project Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        Project project = JsonSerializer.Deserialize<Project>(json, Options)
            ?? throw new JsonException("The document is null rather than a project.");

        // A file need not list clips in start order or tracks in stacking order, but everything
        // that reads them assumes both.
        return ProjectNormalizer.Normalize(project);
    }

    /// <summary>Writes a project to canonical JSON text, ending with a newline.</summary>
    public static string Serialize(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        // A trailing newline is what every other text file has, and what stops git reporting
        // "no newline at end of file" on every save.
        return JsonSerializer.Serialize(project, Options) + "\n";
    }

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions
        {
            // Two spaces, LF, and no BOM, on every platform. Windows-only or not, a project file
            // that changes its line endings when it moves between machines is a bad diff.
            WriteIndented = true,
            IndentCharacter = ' ',
            IndentSize = 2,
            NewLine = "\n",

            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,

            // Absent means "take the default", so an optional reference that is null is left out
            // rather than written as an explicit null. Value-typed members are always written,
            // because a record's default for one is often not default(T): Clip.Enabled defaults
            // to true, so omitting false would read back as true.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

            // Titles and marker notes hold real text. Escaping every non-ASCII character would
            // make a file full of Japanese unreadable to the person editing it.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

            // A hand-edited file picks up comments and stray commas. Reading them is a kindness;
            // jazz fmt takes them back out.
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,

            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = { DropComputedProperties, SkipEmptyArrays, PutSchemaVersionFirst },
            },
        };

        options.Converters.Add(new FlicksConverter());
        options.Converters.Add(new RationalConverter());
        options.Converters.Add(new TimeRangeConverter());
        options.Converters.Add(new Vector2Converter());
        options.Converters.Add(new ParamValueConverter());
        options.Converters.Add(new AnimatedValueConverter());
        options.Converters.Add(new EquatableArrayConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        options.MakeReadOnly();
        return options;
    }

    /// <summary>
    /// Removes properties that compute rather than store.
    /// </summary>
    /// <remarks>
    /// A record's state is exactly its settable properties. Everything else on the model is
    /// derived: <see cref="Clip.Duration"/>, <see cref="Clip.SourceOut"/>, <see cref="Fade.IsNone"/>
    /// and a couple of dozen others. Writing them would bloat the file, invite a hand editor to
    /// change one and expect it to take effect, and break the round trip the moment a derived
    /// value disagreed with what it was derived from. Doing it here rather than with
    /// <c>[JsonIgnore]</c> on each one keeps serialization attributes out of the domain model and
    /// covers every property added from now on without anyone remembering to.
    /// </remarks>
    internal static void DropComputedProperties(JsonTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (type.Kind != JsonTypeInfoKind.Object || !IsModelType(type.Type))
        {
            return;
        }

        for (int index = type.Properties.Count - 1; index >= 0; index--)
        {
            if (type.Properties[index].Set is null)
            {
                type.Properties.RemoveAt(index);
            }
        }
    }

    /// <summary>
    /// Leaves an empty collection out of the file rather than writing <c>[]</c>.
    /// </summary>
    /// <remarks>
    /// Most clips have no effects and no markers, and most tracks have no transitions. An absent
    /// array reads back as the default, which every collection in the model defines as empty, so
    /// this is lossless.
    /// </remarks>
    internal static void SkipEmptyArrays(JsonTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (type.Kind != JsonTypeInfoKind.Object || !IsModelType(type.Type))
        {
            return;
        }

        foreach (JsonPropertyInfo property in type.Properties)
        {
            if (!IsEquatableArray(property.PropertyType))
            {
                continue;
            }

            // The predicate is closed over the element type so the check is a struct test rather
            // than a boxed enumeration on every property of every clip.
            MethodInfo check = NotEmptyMethod.MakeGenericMethod(property.PropertyType.GetGenericArguments()[0]);
            property.ShouldSerialize = check.CreateDelegate<Func<object, object?, bool>>();
        }
    }

    private static readonly MethodInfo NotEmptyMethod =
        typeof(JazzJson).GetMethod(nameof(NotEmpty), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static bool NotEmpty<T>(object owner, object? value)
        where T : IEquatable<T> =>
        value is EquatableArray<T> { IsEmpty: false };

    /// <summary>
    /// Puts <c>schemaVersion</c> at the top of the file.
    /// </summary>
    /// <remarks>
    /// It is the first thing a reader needs and the first thing a person opening the file wants
    /// to see. The model declares it third, after the identifier and the name, because that is
    /// the order it belongs in as a record.
    /// </remarks>
    internal static void PutSchemaVersionFirst(JsonTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (type.Type != typeof(Project))
        {
            return;
        }

        foreach (JsonPropertyInfo property in type.Properties)
        {
            if (string.Equals(property.Name, "schemaVersion", StringComparison.Ordinal))
            {
                property.Order = -1;
            }
        }
    }

    /// <summary>True for the project model's own types, which are the ones these rules apply to.</summary>
    private static bool IsModelType(Type type) =>
        type.Namespace is not null
        && type.Namespace.StartsWith("JazzHands.Core.", StringComparison.Ordinal);

    private static bool IsEquatableArray(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(EquatableArray<>);

    /// <summary>The assembly-qualified marker used by the schema generator to find the model.</summary>
    internal static Assembly ModelAssembly => typeof(Project).Assembly;
}
