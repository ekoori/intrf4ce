using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Intrf4ce.Core.Model;

/// <summary>
/// A list of numbers that may also be written as one bare number: <c>"size": 0.9</c> as well as
/// <c>"size": [0.9]</c> or <c>[1, 2, 3]</c>. Written back, it is always a list.
/// </summary>
/// <remarks>
/// For a field the agent is told can be "one number": told that, an agent writes the number, and
/// refusing it for want of brackets would cost a round trip to learn nothing. Numbers in quotes
/// are read as numbers, as <see cref="DeckJson.Options"/> reads them everywhere else.
/// </remarks>
public sealed class NumbersConverter : JsonConverter<double[]>
{
    public override double[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.StartArray:
                var numbers = new List<double>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    numbers.Add(Number(ref reader));
                }

                return [.. numbers];

            default:
                return [Number(ref reader)];
        }
    }

    public override void Write(Utf8JsonWriter writer, double[] value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartArray();
        foreach (var number in value)
        {
            writer.WriteNumberValue(number);
        }

        writer.WriteEndArray();
    }

    private static double Number(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.Number => reader.GetDouble(),
        JsonTokenType.String when double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        _ => throw new JsonException("expected a number, or a list of numbers"),
    };
}
