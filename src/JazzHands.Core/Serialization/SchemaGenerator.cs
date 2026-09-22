using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Serialization;

/// <summary>
/// Builds the JSON schema for a .jazz file from the model itself.
/// </summary>
/// <remarks>
/// The schema is derived from the same <see cref="JsonTypeInfo"/> the serializer uses, so it
/// describes what this build actually writes rather than what someone remembered to type into a
/// schema file. A hand-maintained schema drifts from the code within about two phases, and a
/// schema that lies is worse than none: it rejects good files and accepts broken ones.
///
/// <c>Docs/schema/jazz-project.schema.json</c> is the published copy, written by
/// <c>tools/gen-schema</c>. A test compares it against this generator's output, so a model change
/// that was not published fails the build rather than shipping a stale document.
///
/// Unknown members are deliberately allowed: the schema never sets <c>additionalProperties</c> to
/// false, because a member this build does not know is a warning and a preserved value, not an
/// error. See <see cref="UnknownFields"/>.
/// </remarks>
public static class SchemaGenerator
{
    /// <summary>The identifier the published schema carries.</summary>
    public const string SchemaId = "https://jazzhands.dev/schema/jazz-project.schema.json";

    private static readonly Lazy<string> LazyText = new(() => Render(Generate()));

    /// <summary>The schema as canonical text, ending with a newline.</summary>
    public static string Text => LazyText.Value;

    /// <summary>Builds the schema document.</summary>
    public static JsonObject Generate()
    {
        var definitions = new JsonObject();
        var seen = new Dictionary<Type, string>();

        foreach ((string name, JsonObject schema) in PrimitiveDefinitions())
        {
            definitions[name] = schema;
        }

        // Keyframe is reachable only through AnimatedValue, which a converter handles, so the
        // walk from Project never meets it. Without this its definition would be missing and the
        // reference from animatedValue would dangle.
        DefineObject(typeof(Keyframe), definitions, seen);

        JsonObject root = ObjectSchema(typeof(Project), definitions, seen);

        var document = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = SchemaId,
            ["title"] = "Jazz Hands project (.jazz)",
            ["description"] =
                "Generated from the JazzHands.Core model by tools/gen-schema. Do not edit by hand: "
                + "change the records and regenerate. Members not described here are preserved on "
                + "save and reported as warnings rather than rejected.",
        };

        foreach ((string name, JsonNode? value) in root)
        {
            document[name] = value?.DeepClone();
        }

        document["$defs"] = Sorted(definitions);
        return document;
    }

    /// <summary>Writes a schema document the way the published file is written.</summary>
    public static string Render(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentCharacter = ' ',
            IndentSize = 2,
            NewLine = "\n",
        };

        return schema.ToJsonString(options) + "\n";
    }

    /// <summary>The types that have a hand-written shape because a converter decides their format.</summary>
    private static IEnumerable<(string Name, JsonObject Schema)> PrimitiveDefinitions()
    {
        yield return ("flicks", new JsonObject
        {
            ["type"] = "integer",
            ["description"] = "A time in flicks, 1/705600000 s. One frame at 30 fps is 23520000.",
        });

        yield return ("rational", new JsonObject
        {
            ["type"] = "object",
            ["description"] = "An exact ratio. Frame rates are written this way so that 30000/1001 stays exact.",
            ["required"] = new JsonArray("num", "den"),
            ["properties"] = new JsonObject
            {
                ["num"] = new JsonObject { ["type"] = "integer" },
                ["den"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
            },
        });

        yield return ("timeRange", new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("start", "duration"),
            ["properties"] = new JsonObject
            {
                ["start"] = Reference("flicks"),
                ["duration"] = Reference("flicks"),
            },
        });

        yield return ("vector2", new JsonObject
        {
            ["type"] = "array",
            ["description"] = "Two numbers, [x, y].",
            ["items"] = new JsonObject { ["type"] = "number" },
            ["minItems"] = 2,
            ["maxItems"] = 2,
        });

        yield return ("paramValue", new JsonObject
        {
            ["description"] =
                "The value of an animatable parameter. A number is a float, a boolean is a bool, "
                + "a string is text, an array of two or four numbers is a vector, and everything "
                + "else carries a type tag.",
            ["anyOf"] = new JsonArray(
                new JsonObject { ["type"] = "number" },
                new JsonObject { ["type"] = "boolean" },
                new JsonObject { ["type"] = "string" },
                new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "number" },
                    ["minItems"] = 2,
                    ["maxItems"] = 4,
                },
                new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("type", "value"),
                    ["properties"] = new JsonObject
                    {
                        ["type"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("float", "float2", "float4", "color", "bool", "int", "enum", "path", "text"),
                        },
                    },
                }),
        });

        yield return ("animatedValue", new JsonObject
        {
            ["description"] = "Either a parameter value, or an object with a keyframes array.",
            ["anyOf"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("keyframes"),
                    ["properties"] = new JsonObject
                    {
                        ["keyframes"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = Reference("keyframe"),
                        },
                    },
                },
                Reference("paramValue")),
        });
    }

    /// <summary>Builds the schema for a model record, adding everything it references to the defs.</summary>
    private static JsonObject ObjectSchema(Type type, JsonObject definitions, Dictionary<Type, string> seen)
    {
        JsonTypeInfo info = JazzJson.Options.GetTypeInfo(type);
        HashSet<string> required = RequiredProperties(type);

        var properties = new JsonObject();
        var requiredNames = new JsonArray();

        foreach (JsonPropertyInfo property in info.Properties.OrderBy(p => p.Order))
        {
            properties[property.Name] = PropertySchema(property, definitions, seen);

            if (required.Contains(property.Name))
            {
                requiredNames.Add(property.Name);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (requiredNames.Count > 0)
        {
            schema["required"] = requiredNames;
        }

        return schema;
    }

    /// <summary>
    /// The members a document must carry.
    /// </summary>
    /// <remarks>
    /// A record's constructor says it: a parameter with no default has to come from the file.
    /// Collections and nullable members are excluded even when they have no default, because the
    /// writer leaves an empty collection and a null out, and a schema that demanded them would
    /// reject this build's own output.
    /// </remarks>
    private static HashSet<string> RequiredProperties(Type type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        ConstructorInfo[] constructors = type.GetConstructors();
        if (constructors.Length != 1)
        {
            return names;
        }

        foreach (ParameterInfo parameter in constructors[0].GetParameters())
        {
            if (parameter.HasDefaultValue || parameter.Name is null)
            {
                continue;
            }

            Type parameterType = parameter.ParameterType;
            if (IsCollection(parameterType) || IsNullable(parameterType))
            {
                continue;
            }

            names.Add(JsonNamingPolicy.CamelCase.ConvertName(parameter.Name));
        }

        return names;
    }

    private static JsonNode PropertySchema(JsonPropertyInfo property, JsonObject definitions, Dictionary<Type, string> seen)
    {
        Type type = property.PropertyType;
        JsonNode schema = TypeSchema(Nullable.GetUnderlyingType(type) ?? type, definitions, seen);

        // A hand editor writes null where this build would leave the member out, so the schema
        // accepts it rather than reporting an error for something harmless.
        if (!AcceptsNull(property))
        {
            return schema;
        }

        return new JsonObject
        {
            ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }),
        };
    }

    /// <summary>
    /// True when the model would accept null for this member.
    /// </summary>
    /// <remarks>
    /// Read from the nullable annotations rather than guessed, because the model is annotated
    /// throughout and a schema that disagrees with the compiler is a schema that rejects files
    /// the loader would have taken.
    /// </remarks>
    private static bool AcceptsNull(JsonPropertyInfo property)
    {
        if (Nullable.GetUnderlyingType(property.PropertyType) is not null)
        {
            return true;
        }

        if (property.PropertyType.IsValueType)
        {
            return false;
        }

        // NullabilityInfoContext is not thread safe, and the schema is generated from tests that
        // may run in parallel, so each question gets its own.
        var context = new NullabilityInfoContext();
        return property.AttributeProvider switch
        {
            PropertyInfo info => context.Create(info).ReadState == NullabilityState.Nullable,
            FieldInfo info => context.Create(info).ReadState == NullabilityState.Nullable,
            _ => false,
        };
    }

    private static JsonNode TypeSchema(Type type, JsonObject definitions, Dictionary<Type, string> seen)
    {
        if (type == typeof(Flicks))
        {
            return Reference("flicks");
        }

        if (type == typeof(Rational))
        {
            return Reference("rational");
        }

        if (type == typeof(TimeRange))
        {
            return Reference("timeRange");
        }

        if (type == typeof(Vector2))
        {
            return Reference("vector2");
        }

        if (type == typeof(ParamValue))
        {
            return Reference("paramValue");
        }

        if (type == typeof(AnimatedValue))
        {
            return Reference("animatedValue");
        }

        if (type == typeof(string))
        {
            return new JsonObject { ["type"] = "string" };
        }

        if (type == typeof(bool))
        {
            return new JsonObject { ["type"] = "boolean" };
        }

        if (type == typeof(int) || type == typeof(long))
        {
            return new JsonObject { ["type"] = "integer" };
        }

        if (type == typeof(float) || type == typeof(double))
        {
            return new JsonObject { ["type"] = "number" };
        }

        if (type == typeof(DateTimeOffset) || type == typeof(DateTime))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        }

        if (type.IsEnum)
        {
            var members = new JsonArray();
            foreach (string name in Enum.GetNames(type))
            {
                members.Add(JsonNamingPolicy.CamelCase.ConvertName(name));
            }

            return new JsonObject { ["type"] = "string", ["enum"] = members };
        }

        if (IsCollection(type))
        {
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = TypeSchema(type.GetGenericArguments()[0], definitions, seen),
            };
        }

        return Reference(DefineObject(type, definitions, seen));
    }

    /// <summary>Adds a record's schema to the defs, once, and returns the name it went in under.</summary>
    private static string DefineObject(Type type, JsonObject definitions, Dictionary<Type, string> seen)
    {
        if (seen.TryGetValue(type, out string? existing))
        {
            return existing;
        }

        string name = JsonNamingPolicy.CamelCase.ConvertName(type.Name);

        // Recorded before the walk, so a type that refers to itself, as a sequence does through
        // its compound clips, does not recurse for ever.
        seen[type] = name;
        definitions[name] = ObjectSchema(type, definitions, seen);
        return name;
    }

    private static bool IsCollection(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(EquatableArray<>);

    private static bool IsNullable(Type type) => Nullable.GetUnderlyingType(type) is not null;

    private static JsonObject Reference(string name) => new() { ["$ref"] = $"#/$defs/{name}" };

    private static JsonObject Sorted(JsonObject definitions)
    {
        var sorted = new JsonObject();
        foreach (string name in definitions.Select(pair => pair.Key).Order(StringComparer.Ordinal))
        {
            sorted[name] = definitions[name]?.DeepClone();
        }

        return sorted;
    }
}
