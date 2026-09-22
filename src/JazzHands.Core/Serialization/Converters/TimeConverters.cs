using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using JazzHands.Core.Time;

namespace JazzHands.Core.Serialization.Converters;

/// <summary>
/// Writes <see cref="Flicks"/> as a plain integer.
/// </summary>
/// <remarks>
/// A flick is 1/705,600,000 s and the count always fits in a long, so the file holds the exact
/// number with no unit and no rounding. Anyone hand-editing a project can compute one: a frame at
/// 30 fps is 23,520,000 flicks, and `jazz` prints the conversion. Writing seconds here would make
/// the file lossy, which is the whole reason the time model exists.
/// </remarks>
public sealed class FlicksConverter : JsonConverter<Flicks>
{
    /// <inheritdoc />
    public override Flicks Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetInt64());

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Flicks value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(value.Value);
    }
}

/// <summary>
/// Writes <see cref="Rational"/> as <c>{"num": 30000, "den": 1001}</c>.
/// </summary>
/// <remarks>
/// Never as a decimal. 29.97 is not a frame rate; 30000/1001 is, and the difference accumulates
/// to a frame every thousand frames. The object form also reads better by hand than a string,
/// and a hand-written unnormalized pair such as 60/2 is normalized on load.
/// </remarks>
public sealed class RationalConverter : JsonConverter<Rational>
{
    /// <inheritdoc />
    public override Rational Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A frame rate is an object with num and den, for example {\"num\": 30000, \"den\": 1001}.");
        }

        long num = 0;
        long den = 1;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            bool isNum = reader.ValueTextEquals("num");
            bool isDen = reader.ValueTextEquals("den");
            reader.Read();

            if (isNum)
            {
                num = reader.GetInt64();
            }
            else if (isDen)
            {
                den = reader.GetInt64();
            }
            else
            {
                reader.Skip();
            }
        }

        if (den == 0)
        {
            throw new JsonException("A frame rate cannot have a zero denominator.");
        }

        return new Rational(num, den);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Rational value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber("num", value.Num);
        writer.WriteNumber("den", value.Den);
        writer.WriteEndObject();
    }
}

/// <summary>Writes <see cref="TimeRange"/> as <c>{"start": 0, "duration": 23520000}</c>.</summary>
/// <remarks>
/// Start and duration rather than start and end, because that is how the model stores it and
/// because a duration survives a move without arithmetic.
/// </remarks>
public sealed class TimeRangeConverter : JsonConverter<TimeRange>
{
    /// <inheritdoc />
    public override TimeRange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A time range is an object with start and duration in flicks.");
        }

        long start = 0;
        long duration = 0;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            bool isStart = reader.ValueTextEquals("start");
            bool isDuration = reader.ValueTextEquals("duration");
            reader.Read();

            if (isStart)
            {
                start = reader.GetInt64();
            }
            else if (isDuration)
            {
                duration = reader.GetInt64();
            }
            else
            {
                reader.Skip();
            }
        }

        return new TimeRange(new Flicks(start), new Flicks(duration));
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TimeRange value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber("start", value.Start.Value);
        writer.WriteNumber("duration", value.Duration.Value);
        writer.WriteEndObject();
    }
}

/// <summary>Writes <see cref="Vector2"/> as <c>[x, y]</c>.</summary>
/// <remarks>
/// The default shape would be <c>{"X": 0, "Y": 0}</c>, with capitals that fit nothing else in
/// the file. Bezier handles and positions read better as pairs anyway.
/// </remarks>
public sealed class Vector2Converter : JsonConverter<Vector2>
{
    /// <inheritdoc />
    public override Vector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("A two component vector is an array of two numbers, for example [0.5, 1].");
        }

        reader.Read();
        float x = reader.GetSingle();
        reader.Read();
        float y = reader.GetSingle();
        reader.Read();

        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("A two component vector has exactly two numbers.");
        }

        return new Vector2(x, y);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Vector2 value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteEndArray();
    }
}
