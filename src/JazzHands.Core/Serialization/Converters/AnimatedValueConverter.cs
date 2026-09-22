using System.Text.Json;
using System.Text.Json.Serialization;
using JazzHands.Core.Model;

namespace JazzHands.Core.Serialization.Converters;

/// <summary>
/// Reads and writes <see cref="AnimatedValue"/>: either a bare parameter value or a keyframe list.
/// </summary>
/// <remarks>
/// A parameter that never moves is written as its value and nothing else, because that is what
/// almost every parameter in a project is and a wrapper object around each one would triple the
/// size of the file for nothing:
///
/// <code>
/// "opacity": 1
/// "opacity": {"keyframes": [{"time": 0, "value": 0}, {"time": 23520000, "value": 1}]}
/// </code>
///
/// The two are told apart by the <c>keyframes</c> member, which no parameter value has.
/// </remarks>
public sealed class AnimatedValueConverter : JsonConverter<AnimatedValue>
{
    /// <inheritdoc />
    public override AnimatedValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            // Peek for a keyframes member without consuming the reader, so a static object value
            // such as a colour can still be handed to the parameter converter intact.
            Utf8JsonReader peek = reader;
            if (HasKeyframes(ref peek))
            {
                return ReadKeyframed(ref reader, options);
            }
        }

        JsonConverter<ParamValue> values = ValueConverter(options);
        ParamValue value = values.Read(ref reader, typeof(ParamValue), options)
            ?? throw new JsonException("A parameter value cannot be null.");

        return AnimatedValue.Constant(value);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, AnimatedValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);

        switch (value)
        {
            case StaticValue still:
                ValueConverter(options).Write(writer, still.Value, options);
                break;

            case KeyframedValue animated:
                writer.WriteStartObject();
                writer.WritePropertyName("keyframes");
                JsonSerializer.Serialize(writer, animated.Keyframes, options);
                writer.WriteEndObject();
                break;

            default:
                throw new JsonException($"There is no file format for animated values of type {value.GetType().Name}.");
        }
    }

    private static JsonConverter<ParamValue> ValueConverter(JsonSerializerOptions options) =>
        (JsonConverter<ParamValue>)options.GetConverter(typeof(ParamValue));

    private static bool HasKeyframes(ref Utf8JsonReader reader)
    {
        int depth = 0;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    depth++;
                    break;

                case JsonTokenType.EndArray:
                    depth--;
                    break;

                case JsonTokenType.EndObject:
                    if (depth == 0)
                    {
                        return false;
                    }

                    depth--;
                    break;

                case JsonTokenType.PropertyName when depth == 0 && reader.ValueTextEquals("keyframes"):
                    return true;
            }
        }

        return false;
    }

    private static AnimatedValue ReadKeyframed(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        EquatableArray<Keyframe> keyframes = EquatableArray<Keyframe>.Empty;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            bool isKeyframes = reader.ValueTextEquals("keyframes");
            reader.Read();

            if (isKeyframes)
            {
                keyframes = JsonSerializer.Deserialize<EquatableArray<Keyframe>>(ref reader, options);
            }
            else
            {
                reader.Skip();
            }
        }

        return new KeyframedValue(keyframes);
    }
}
