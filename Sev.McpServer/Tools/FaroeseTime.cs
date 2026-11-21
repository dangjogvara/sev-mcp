using System.Globalization;
using Sev.Client;

namespace Sev.McpServer.Tools;

// SEV works in local Faroese time and SevClient only accepts DateTime with Kind = Unspecified.
// Claude may send plain dates, local times, or ISO strings with "Z" or an offset; this turns all of
// them into Faroese wall-clock time.
public static class FaroeseTime
{
    public static bool TryParse(string? text, out DateTime localTime)
    {
        localTime = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // RoundtripKind reports whether the text had "Z" (Utc), an offset (Local) or neither (Unspecified).
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return false;
        }

        if (parsed.Kind == DateTimeKind.Unspecified)
        {
            localTime = parsed;
            return true;
        }

        // Parse again keeping the original offset, then convert to Faroese time.
        var withOffset = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
        localTime = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(withOffset, SevTime.Zone).DateTime, DateTimeKind.Unspecified);
        return true;
    }
}
