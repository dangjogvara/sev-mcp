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

        // DateTimeOffset keeps the date and time as written, whatever the offset ("...Z", "+02:00"); DateTime would
        // convert an explicit offset to the machine's own time zone and could move it to another day.
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dateTime))
        {
            return DateOnly.FromDateTime(dateTime.DateTime);
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
