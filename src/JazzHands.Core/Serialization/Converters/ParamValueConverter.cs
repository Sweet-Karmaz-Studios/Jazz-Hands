using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using JazzHands.Core.Model;

namespace JazzHands.Core.Serialization.Converters;

/// <summary>
/// Reads and writes <see cref="ParamValue"/>, the closed union behind every animatable parameter.
/// </summary>
/// <remarks>
/// The shape has to be unambiguous, because nothing in the file says what type a parameter is:
/// the effect registry knows, and it does not exist while the file is being parsed. A bare
/// <c>3</c> could be a float or an int, and a bare string could be text, an enum member or an SVG
/// path, so those carry a tag. The common cases do not:
///
/// <code>
/// 0.5                                  float
/// true                                 bool
/// [0, 0]                               float2
/// [0, 0, 1, 1]                         float4
/// "Wishlist now"                       text
/// {"type": "int", "value": 3}          int
/// {"type": "enum", "value": "screen"}  enum
/// {"type": "path", "value": "M0,0"}    path
/// {"type": "color", "value": [r,g,b,a]} color, linear premultiplied RGBA
/// </code>
///
/// Every tagged form is also accepted for the shorthand types, so a hand editor can write
/// <c>{"type": "float", "value": 1}</c> and be understood. It is rewritten to the shorthand by
/// <c>jazz fmt</c>.
/// </remarks>
public sealed class ParamValueConverter : JsonConverter<ParamValue>
{
    /// <inheritdoc />
    public override ParamValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return new ParamValue.Float(reader.GetSingle());

            case JsonTokenType.True:
            case JsonTokenType.False:
                return new ParamValue.Bool(reader.GetBoolean());

            case JsonTokenType.String:
                return new ParamValue.Text(reader.GetString() ?? string.Empty);

            case JsonTokenType.StartArray:
                return ReadVector(ref reader, tag: null);

            case JsonTokenType.StartObject:
                return ReadTagged(ref reader);

            default:
                throw new JsonException($"A parameter value cannot be {reader.TokenType}.");
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ParamValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        switch (value)
        {
            case ParamValue.Float number:
                writer.WriteNumberValue(number.Value);
                break;

            case ParamValue.Bool flag:
                writer.WriteBooleanValue(flag.Value);
                break;

            case ParamValue.Text text:
                writer.WriteStringValue(text.Value);
                break;

            case ParamValue.Float2 pair:
                WriteNumbers(writer, pair.Value.X, pair.Value.Y);
                break;

            case ParamValue.Float4 quad:
                WriteNumbers(writer, quad.Value.X, quad.Value.Y, quad.Value.Z, quad.Value.W);
                break;

            case ParamValue.Color color:
                writer.WriteStartObject();
                writer.WriteString("type", "color");
                writer.WritePropertyName("value");
                WriteNumbers(writer, color.Value.X, color.Value.Y, color.Value.Z, color.Value.W);
                writer.WriteEndObject();
                break;

            case ParamValue.Int whole:
                WriteTagged(writer, "int", whole.Value);
                break;

            case ParamValue.Enum member:
                WriteTagged(writer, "enum", member.Value);
                break;

            case ParamValue.Path path:
                WriteTagged(writer, "path", path.Value);
                break;

            default:
                throw new JsonException($"There is no file format for parameter values of type {value.GetType().Name}.");
        }
    }

    private static void WriteNumbers(Utf8JsonWriter writer, params ReadOnlySpan<float> values)
    {
        writer.WriteStartArray();
        foreach (float component in values)
        {
            writer.WriteNumberValue(component);
        }

        writer.WriteEndArray();
    }

    private static void WriteTagged(Utf8JsonWriter writer, string tag, int value)
    {
        writer.WriteStartObject();
        writer.WriteString("type", tag);
        writer.WriteNumber("value", value);
        writer.WriteEndObject();
    }

    private static void WriteTagged(Utf8JsonWriter writer, string tag, string value)
    {
        writer.WriteStartObject();
        writer.WriteString("type", tag);
        writer.WriteString("value", value);
        writer.WriteEndObject();
    }

    /// <summary>Reads an array of two or four numbers into the matching vector type.</summary>
    private static ParamValue ReadVector(ref Utf8JsonReader reader, string? tag)
    {
        Span<float> components = stackalloc float[4];
        int count = 0;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (count == 4)
            {
                throw new JsonException("A vector parameter has two or four numbers, not more.");
            }

            components[count++] = reader.GetSingle();
        }

        return (count, tag) switch
        {
            (2, null or "float2") => new ParamValue.Float2(new Vector2(components[0], components[1])),
            (4, "color") => new ParamValue.Color(new Vector4(components[0], components[1], components[2], components[3])),
            (4, null or "float4") => new ParamValue.Float4(new Vector4(components[0], components[1], components[2], components[3])),
            _ => throw new JsonException($"A {tag ?? "vector"} parameter cannot have {count} components."),
        };
    }

    /// <summary>Reads the <c>{"type": ..., "value": ...}</c> form.</summary>
    private static ParamValue ReadTagged(ref Utf8JsonReader reader)
    {
        string? tag = null;
        ParamValue? value = null;
        bool sawValue = false;

        // The tag may follow the value in a hand-edited file, so the value is buffered until the
        // object closes rather than interpreted as it arrives.
        JsonElement? pending = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            bool isType = reader.ValueTextEquals("type");
            bool isValue = reader.ValueTextEquals("value");
            reader.Read();

            if (isType)
            {
                tag = reader.GetString();
            }
            else if (isValue)
            {
                pending = JsonElement.ParseValue(ref reader);
                sawValue = true;
            }
            else
            {
                reader.Skip();
            }
        }

        if (!sawValue || pending is not { } element)
        {
            throw new JsonException("A tagged parameter value needs a value member.");
        }

        value = FromElement(element, tag);
        return value;
    }

    private static ParamValue FromElement(JsonElement element, string? tag) => tag switch
    {
        "float" => new ParamValue.Float(element.GetSingle()),
        "bool" => new ParamValue.Bool(element.GetBoolean()),
        "int" => new ParamValue.Int(element.GetInt32()),
        "enum" => new ParamValue.Enum(element.GetString() ?? string.Empty),
        "path" => new ParamValue.Path(element.GetString() ?? string.Empty),
        "text" => new ParamValue.Text(element.GetString() ?? string.Empty),
        "float2" or "float4" or "color" => ReadVectorElement(element, tag),
        null => throw new JsonException("A tagged parameter value needs a type member, for example \"int\" or \"enum\"."),
        _ => throw new JsonException($"'{tag}' is not a parameter type. Expected one of float, float2, float4, color, bool, int, enum, path or text."),
    };

    private static ParamValue ReadVectorElement(JsonElement element, string tag)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"A {tag} parameter's value is an array of numbers.");
        }

        Utf8JsonReader reader = new(System.Text.Encoding.UTF8.GetBytes(element.GetRawText()));
        reader.Read();
        return ReadVector(ref reader, tag);
    }
}
