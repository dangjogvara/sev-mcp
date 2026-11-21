using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sev.Client.Json;

// The spec types meter dates as free-form nullable strings. Real data uses "yyyy-MM-dd",
// but one odd value must not fail the whole get_available_meters response, so anything
// unparseable becomes null.
internal class LenientDateOnlyConverter : JsonConverter<DateOnly?>
{
    public override bool HandleNull => true;

    public override DateOnly? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return null;
        }

        var text = reader.GetString();

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        // RoundtripKind keeps the date as written instead of converting "...Z" to the machine's local time.
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime))
        {
            return DateOnly.FromDateTime(dateTime);
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, DateOnly? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
    }
}
