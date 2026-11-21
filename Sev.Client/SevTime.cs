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
}
