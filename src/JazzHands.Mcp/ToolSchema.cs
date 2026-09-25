using System.Collections;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;

namespace JazzHands.Mcp;

/// <summary>
/// Tool names and JSON schemas for registry commands, written for a model to read.
/// </summary>
/// <remarks>
/// A tool's params are the command's JSON args, the same object a <c>jazz apply</c> step or an RPC
/// call carries. Times, ranges, rates and sizes are strings in the forms the command line takes,
/// and every schema says which; enums list their values as the project file writes them.
/// </remarks>
public static class ToolSchema
{
    /// <summary>The longest tool description sent; detail lives in <c>jazz://docs</c>.</summary>
    public const int MaxDescription = 300;

    /// <summary>A registry name as a tool name: <c>clip.set-opacity</c> is <c>clip_set_opacity</c>.</summary>
    public static string ToolName(string method) => method.Replace('.', '_').Replace('-', '_');

    /// <summary>The tool description of a registry entry: what it does, whether it undoes, where to read more.</summary>
    public static string Description(CommandMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        string undo = metadata.IsQuery
            ? " Read only."
            : metadata.Undoable ? " One undo step." : $" Not undoable: {metadata.NotUndoableReason.TrimEnd('.')}.";
        string text = $"{metadata.Description.TrimEnd('.')}.{undo} Docs: jazz://docs/{JazzTools.DocArea(metadata.Area)}";
        return Clip(text);
    }

    /// <summary>Shortens text to <see cref="MaxDescription"/> characters at a word.</summary>
    public static string Clip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= MaxDescription)
        {
            return text;
        }

        int cut = text.LastIndexOf(' ', MaxDescription - 4);
        return string.Concat(text.AsSpan(0, cut < 0 ? MaxDescription - 3 : cut), "...");
    }

    /// <summary>The input schema of a registry entry.</summary>
    public static JsonObject For(CommandMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (ParameterMetadata parameter in metadata.Parameters)
        {
            JsonObject property = Property(parameter.Type, parameter.Description);
            if (!parameter.IsRequired && Default(parameter.DefaultValue) is { } fallback)
            {
                property["default"] = fallback;
            }

            properties[parameter.JsonName] = property;
            if (parameter.IsRequired)
            {
                required.Add(parameter.JsonName);
            }
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        schema["additionalProperties"] = false;
        return schema;
    }

    /// <summary>The schema of one value of a type, with its description.</summary>
    public static JsonObject Property(Type type, string description)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type bare = Nullable.GetUnderlyingType(type) ?? type;
        JsonObject schema = Shape(bare, out string forms);
        string text = (description ?? string.Empty).TrimEnd('.');
        if (forms.Length > 0)
        {
            text = text.Length > 0 ? $"{text}. {forms}" : forms;
        }

        if (text.Length > 0)
        {
            schema["description"] = text;
        }

        return schema;
    }

    private static JsonObject Shape(Type bare, out string forms)
    {
        forms = string.Empty;
        if (bare == typeof(string))
        {
            return new JsonObject { ["type"] = "string" };
        }

        if (bare == typeof(bool))
        {
            return new JsonObject { ["type"] = "boolean" };
        }

        if (bare == typeof(int) || bare == typeof(long) || bare == typeof(short) || bare == typeof(uint))
        {
            return new JsonObject { ["type"] = "integer" };
        }

        if (bare == typeof(double) || bare == typeof(float) || bare == typeof(decimal))
        {
            return new JsonObject { ["type"] = "number" };
        }

        if (bare == typeof(Flicks))
        {
            forms = "A time: 00:00:02.500, 2.5s, 75f (frames) or an integer of flicks";
            return new JsonObject { ["type"] = new JsonArray("string", "integer") };
        }

        if (bare == typeof(TimeRange))
        {
            forms = "A range: 00:00:10.000-00:00:25.000, 10s-25s or 300f-750f";
            return new JsonObject { ["type"] = "string" };
        }

        if (bare == typeof(Rational))
        {
            forms = "A rate or ratio: 30000/1001, 60, 29.97, or a number such as 0.5 or 2";
            return new JsonObject { ["type"] = new JsonArray("string", "number") };
        }

        if (bare == typeof(FrameSize))
        {
            forms = "A size: 1920x1080, 1080p, 720p or 4k";
            return new JsonObject { ["type"] = "string" };
        }

        if (bare.IsEnum)
        {
            return new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray([.. Enum.GetNames(bare).Select(name => (JsonNode?)JsonNamingPolicy.CamelCase.ConvertName(name))]),
            };
        }

        if (bare == typeof(ICommand[]))
        {
            forms = "Each step is {\"command\": \"clip.split\", \"args\": {...}}";
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["command"] = new JsonObject { ["type"] = "string" },
                        ["args"] = new JsonObject { ["type"] = "object" },
                    },
                    ["required"] = new JsonArray("command"),
                },
            };
        }

        if (ItemType(bare) is { } item)
        {
            JsonObject items = Shape(Nullable.GetUnderlyingType(item) ?? item, out string itemForms);
            forms = itemForms.Length > 0 ? $"Each item: {char.ToLowerInvariant(itemForms[0])}{itemForms[1..]}" : string.Empty;
            return new JsonObject { ["type"] = "array", ["items"] = items };
        }

        if (bare == typeof(JsonNode) || bare == typeof(JsonObject) || bare == typeof(JsonElement) || bare == typeof(object))
        {
            return [];
        }

        // A record: what System.Text.Json would read, from the project file's own options.
        try
        {
            return JsonSchemaExporter.GetJsonSchemaAsNode(JazzJson.Options, bare) as JsonObject ?? [];
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
        {
            return [];
        }
    }

    private static Type? ItemType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        if (type.IsGenericType)
        {
            Type definition = type.GetGenericTypeDefinition();
            if (definition == typeof(EquatableArray<>) || definition == typeof(ImmutableArray<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(List<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        return null;
    }

    private static JsonNode? Default(object? value) => value switch
    {
        null => null,
        string text => text.Length > 0 ? JsonValue.Create(text) : null,
        bool flag => JsonValue.Create(flag),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        Enum choice => JsonValue.Create(JsonNamingPolicy.CamelCase.ConvertName(choice.ToString())),
        IEnumerable => null,
        _ => null,
    };
}
