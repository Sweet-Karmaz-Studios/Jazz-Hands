using System.Text.Json;
using System.Text.Json.Serialization;
using JazzHands.Core.Model;

namespace JazzHands.Core.Serialization.Converters;

/// <summary>
/// Reads and writes <see cref="AnimatedValue"/>: a bare parameter value, a keyframe list, or a driver.
/// </summary>
/// <remarks>
/// A parameter that never moves is written as its value and nothing else, because that is what
/// almost every parameter in a project is and a wrapper object around each one would triple the
/// size of the file for nothing:
///
/// <code>
/// "opacity": 1
/// "opacity": {"keyframes": [{"time": 0, "value": 0}, {"time": 23520000, "value": 1}]}
/// "opacity": {"driver": "0.5 + audio(\"Music\", low) * 0.5", "base": 1}
/// </code>
///
/// They are told apart by the <c>keyframes</c> and <c>driver</c> members, which no parameter value has.
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
            switch (Marker(ref peek))
            {
                case "keyframes":
                    return ReadKeyframed(ref reader, options);
                case "driver":
                    return ReadDriven(ref reader, options);
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

            case DrivenValue driven:
                writer.WriteStartObject();
                writer.WriteString("driver", driven.Expression);
                writer.WritePropertyName("base");
                Write(writer, driven.Base, options);
                writer.WriteEndObject();
                break;

            default:
                throw new JsonException($"There is no file format for animated values of type {value.GetType().Name}.");
        }
    }

    private static JsonConverter<ParamValue> ValueConverter(JsonSerializerOptions options) =>
        (JsonConverter<ParamValue>)options.GetConverter(typeof(ParamValue));

    /// <summary>The member that says what an object is, <c>keyframes</c> or <c>driver</c>, or null for a parameter value.</summary>
    private static string? Marker(ref Utf8JsonReader reader)
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
                        return null;
                    }

                    depth--;
                    break;

                case JsonTokenType.PropertyName when depth == 0 && reader.ValueTextEquals("keyframes"):
                    return "keyframes";

                case JsonTokenType.PropertyName when depth == 0 && reader.ValueTextEquals("driver"):
                    return "driver";
            }
        }

        return null;
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

    private AnimatedValue ReadDriven(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        string expression = string.Empty;
        AnimatedValue? under = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            string name = reader.GetString() ?? string.Empty;
            reader.Read();
            switch (name)
            {
                case "driver":
                    expression = reader.GetString() ?? string.Empty;
                    break;
                case "base":
                    under = Read(ref reader, typeof(AnimatedValue), options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        // A driver with nothing underneath drives a number starting at zero.
        return new DrivenValue(expression, under ?? AnimatedValue.Constant(0.0f));
    }
}
