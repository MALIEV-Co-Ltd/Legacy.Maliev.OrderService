using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Legacy.Maliev.OrderService.Application.Models;

/// <summary>Preserves standard JSON primitive text accepted by the original order-file request reader.</summary>
public sealed class OrderFileScalarStringJsonConverter : JsonConverter<string>
{
    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.True:
                return "true";
            case JsonTokenType.False:
                return "false";
            case JsonTokenType.Number:
                // JsonTextReader.ReadAsString validates the number but retains its lexeme.
                // Numeric ToString would lose trailing zeroes, exponent spelling or negative zero.
                var text = reader.HasValueSequence
                    ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
                    : Encoding.UTF8.GetString(reader.ValueSpan);
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    return text;
                throw new JsonException("Invalid order-file text number.");
            default:
                throw new JsonException("Order-file text requires a JSON string, number, Boolean or null.");
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
