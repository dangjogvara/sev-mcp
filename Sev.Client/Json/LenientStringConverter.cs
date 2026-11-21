using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sev.Client.Json;

// For fields the spec types as "object" although they hold text (meter_nickname).
// A string is read as-is; any other JSON value becomes null instead of throwing.
internal class LenientStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString();
        }

        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
