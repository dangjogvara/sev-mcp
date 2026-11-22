using System.Globalization;
using Sev.Client;

namespace Sev.McpServer.Tools;

// SEV works in local Faroese time and SevClient only accepts DateTime with Kind = Unspecified.
// Claude may send plain dates, local times, or ISO strings with "Z" or an offset; this turns all of
// them into Faroese wall-clock time.
// Only ISO 8601 shapes are accepted: .NET's general parser would also take "06:00" (today, on the server's
// clock) or "11/10/2026" (read the American way, 10 November), and quietly answer for the wrong period.
public static class FaroeseTime
{
    // Without an offset: already Faroese local time. A space works as well as "T" between date and time.
    private static readonly string[] LocalFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF",
    ];

    // With "Z" or an offset such as "+01:00": converted to Faroese time.
    private static readonly string[] OffsetFormats =
    [
        "yyyy-MM-dd'T'HH:mm'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mmzzz", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    ];

    public static bool TryParse(string? text, out DateTime localTime)
    {
        localTime = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        text = text.Trim();

        if (DateTime.TryParseExact(text, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            localTime = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            return true;
        }

        // "Z" is read as UTC (AssumeUniversal); an explicit offset is kept as written.
        if (DateTimeOffset.TryParseExact(
                text, OffsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var withOffset))
        {
            localTime = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(withOffset, SevTime.Zone).DateTime, DateTimeKind.Unspecified);
            return true;
        }

        return false;
    }
}
