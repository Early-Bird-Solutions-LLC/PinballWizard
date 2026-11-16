using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Integrations.Ifpa;

// IFPA's live JSON often encodes numeric fields as strings ("46", "1544.39")
// and occasionally as non-numeric sentinels ("Not Ranked"). System.Text.Json
// rejects those without a converter even when AllowReadingFromString is set
// for the sentinel case. Null on unparseable keeps search hits usable.
internal sealed class FlexibleNullableDoubleConverter : JsonConverter<double?>
{
    public override double? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.String => TryParse(reader.GetString()),
            _ => null,
        };

    public override void Write(
        Utf8JsonWriter writer,
        double? value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteNumberValue(value.Value);
    }

    private static double? TryParse(string? raw) =>
        !string.IsNullOrWhiteSpace(raw)
        && double.TryParse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
}
