namespace Sev.Client;

// SEV works in local Faroese time for both requests and responses.
public static class SevTime
{
    // Needs the system time-zone database; slim container images may lack it (install tzdata).
    private static readonly Lazy<TimeZoneInfo> LazyZone = new(() => TimeZoneInfo.FindSystemTimeZoneById("Atlantic/Faroe"));

    public static TimeZoneInfo Zone => LazyZone.Value;

    // The current Faroese wall-clock time, with Kind = Unspecified like SEV's timestamps.
    public static DateTime LocalNow(TimeProvider time) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone).DateTime, DateTimeKind.Unspecified);

    // 10:37 becomes 10:00. Readings are stamped with the start of their hour, and the current hour has no reading yet.
    public static DateTime StartOfHour(DateTime time) =>
        new(time.Year, time.Month, time.Day, time.Hour, 0, 0, time.Kind);
}
