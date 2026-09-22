using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using JazzHands.Core.Model;

namespace JazzHands.Core.Serialization.Converters;

/// <summary>
/// Reads and writes <see cref="EquatableArray{T}"/> as a plain JSON array.
/// </summary>
/// <remarks>
/// The type is a readonly struct, which System.Text.Json would otherwise treat as an object and
/// write as <c>{"length": 3, "items": [...]}</c>. A missing member deserializes to the default
/// instance, which the model treats as empty, so an absent array and an empty one mean the same
/// thing to a reader.
/// </remarks>
public sealed class EquatableArrayConverter : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        return typeToConvert.IsGenericType
            && typeToConvert.GetGenericTypeDefinition() == typeof(EquatableArray<>);
    }

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        Type element = typeToConvert.GetGenericArguments()[0];
        Type converter = typeof(ArrayConverter<>).MakeGenericType(element);

        return (JsonConverter)Activator.CreateInstance(converter)!;
    }

    private sealed class ArrayConverter<T> : JsonConverter<EquatableArray<T>>
        where T : IEquatable<T>
    {
        public override EquatableArray<T> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return EquatableArray<T>.Empty;
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException($"Expected an array of {typeof(T).Name}.");
            }

            ImmutableArray<T>.Builder items = ImmutableArray.CreateBuilder<T>();

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                T? item = JsonSerializer.Deserialize<T>(ref reader, options);
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            return new EquatableArray<T>(items.ToImmutable());
        }

        public override void Write(Utf8JsonWriter writer, EquatableArray<T> value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);

            writer.WriteStartArray();
            foreach (T item in value)
            {
                JsonSerializer.Serialize(writer, item, options);
            }

            writer.WriteEndArray();
        }
    }
}
