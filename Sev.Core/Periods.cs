using Sev.Client;

namespace Sev.Core;

public enum Granularity
{
    Hour,
    Day,
    Month,
}

// Periods in Faroese local time, shared by the tools that take from/to/granularity (get_usage, get_cost, get_co2,
// get_heatpump_history): reading and checking the arguments, naming the period a time falls in, and listing the
// hours that should have a reading.
public static class Periods
{
    public static TimeSpan MaxRange(Granularity granularity) => granularity switch
    {
        // Hourly values for longer periods would flood Claude's context (about 720 per meter per month).
        Granularity.Hour => TimeSpan.FromDays(31),
        Granularity.Day => TimeSpan.FromDays(366),
        _ => TimeSpan.FromDays(731),
    };

    // Matches the names only; Enum.TryParse would also accept numbers like "1".
    public static Granularity ParseGranularity(string? granularity)
    {
        foreach (var value in Enum.GetValues<Granularity>())
        {
            if (string.Equals(value.ToString(), granularity?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        throw new InvalidInputException($"Unknown granularity '{granularity}'. Use 'hour', 'day' or 'month'.");
    }

    // Validates the dates; InvalidInputException messages say what to fix, for Claude or a person.
    public static (DateTime From, DateTime To) ParseRange(string from, string to, Granularity granularity)
    {
        if (!FaroeseTime.TryParse(from, out var fromTime))
        {
            throw new InvalidInputException($"Could not read 'from' ({from}). Use a date like 2026-10-10 or a date-time like 2026-10-10T06:00.");
        }
        if (!FaroeseTime.TryParse(to, out var toTime))
        {
            throw new InvalidInputException($"Could not read 'to' ({to}). Use a date like 2026-10-11 or a date-time like 2026-10-11T06:00.");
        }
        if (fromTime >= toTime)
        {
            throw new InvalidInputException($"'from' ({fromTime:yyyy-MM-dd HH:mm}) must be before 'to' ({toTime:yyyy-MM-dd HH:mm}); 'to' is not included.");
        }
        var maxRange = MaxRange(granularity);
        if (toTime - fromTime > maxRange)
        {
            throw new InvalidInputException(
                $"Granularity '{granularity.ToString().ToLowerInvariant()}' is limited to {maxRange.TotalDays} days per call. " +
                "Use a shorter period or a coarser granularity ('day' or 'month').");
        }

        return (fromTime, toTime);
    }

    // The period a time falls in, as "2026-10-10T05:00", "2026-10-10" or "2026-10". ISO-style keys sort
    // chronologically as strings.
    public static string Key(DateTime time, Granularity granularity) => granularity switch
    {
        Granularity.Hour => time.ToString("yyyy-MM-dd'T'HH:mm"),
        Granularity.Day => time.ToString("yyyy-MM-dd"),
        _ => time.ToString("yyyy-MM"),
    };

    // The local hour starts in [from, to) that should have a reading by now: hours that have ended,
    // skipping the hour that doesn't exist when summer time starts. On the day summer time ends the
    // repeated hour has the same local label, so it is expected once.
    public static IReadOnlyList<DateTime> ExpectedHours(DateTime from, DateTime to, DateTime localNow)
    {
        var currentHour = SevTime.StartOfHour(localNow);
        var end = to < currentHour ? to : currentHour;
        var hours = new List<DateTime>();
        for (var hour = SevTime.StartOfHour(from); hour < end; hour = hour.AddHours(1))
        {
            if (hour >= from && !SevTime.Zone.IsInvalidTime(hour))
            {
                hours.Add(hour);
            }
        }
        return hours;
    }
}
