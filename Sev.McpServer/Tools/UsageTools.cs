using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sev.Client;
using Sev.Client.Models;

namespace Sev.McpServer.Tools;

public enum Granularity
{
    Hour,
    Day,
    Month,
}

[McpServerToolType]
public class UsageTools(SevDataService sev)
{
    [McpServerTool(Name = "get_usage", Title = "Get electricity usage", ReadOnly = true, Idempotent = true)]
    [Description(
        "Gets electricity consumption in kWh per hour, day or month for the user's meters, with a total per meter. " +
        "Times are local Faroese time and the range is [from, to): 'to' is not included, so yesterday is " +
        "from yesterday 00:00 to today 00:00. " +
        "Use granularity 'day' or 'month' for longer periods, and 'hour' only when hourly detail is needed. " +
        "Limits per call: hour 31 days, day 366 days, month 731 days. " +
        "Leave out 'meters' to get all active meters. " +
        "The SEV API allows only 10 calls per 5 minutes and returns at most 31 days per call, so ask for all meters " +
        "and the whole period in one call instead of several small ones. Long periods may come back partly fetched: " +
        "then a note says which part is still missing and the user can ask again in a few minutes to get the rest. " +
        "Completed past days are cached, so asking about them again is cheap. " +
        "If a total or value is null, or missingHours is above 0, tell the user that data is missing for that period; " +
        "don't report it as zero usage or as a complete total.")]
    public Task<IReadOnlyList<MeterUsageResult>> GetUsageAsync(
        [Description("Start, inclusive. A date (2026-10-10) or date-time (2026-10-10T06:00) in Faroese local time.")] string from,
        [Description("End, exclusive. A date (2026-10-11) or date-time in Faroese local time.")] string to,
        [Description("'hour', 'day' (default) or 'month'.")] string granularity = "day",
        [Description("Meters by ID, nickname, meter name or part of the address, as listed by list_meters. Leave out for all active meters.")] string[]? meters = null,
        CancellationToken cancellationToken = default)
    {
        var bucket = ParseGranularity(granularity);
        var (fromTime, toTime) = ParseRange(from, to, bucket);
        return SevToolErrors.CallAsync(async () =>
        {
            var available = SevToolErrors.ThrowIfEmpty(MeterTools.Summarize(await sev.GetAvailableMetersAsync(cancellationToken)));
            var selected = ResolveMeters(available, meters);

            var result = SevToolErrors.ThrowIfEmptyAnswer(
                await sev.GetHourlyUsageAsync(selected.Select(m => m.MeterId).ToList(), fromTime, toTime, cancellationToken));
            return Summarize(selected, result.Meters, fromTime, toTime, SevTime.LocalNow(TimeProvider.System), bucket, result.NotFetched);
        });
    }

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
        throw new McpException($"Unknown granularity '{granularity}'. Use 'hour', 'day' or 'month'.");
    }

    // Validates the dates; McpException messages are shown to Claude so it can correct the call.
    public static (DateTime From, DateTime To) ParseRange(string from, string to, Granularity granularity)
    {
        if (!FaroeseTime.TryParse(from, out var fromTime))
        {
            throw new McpException($"Could not read 'from' ({from}). Use a date like 2026-10-10 or a date-time like 2026-10-10T06:00.");
        }
        if (!FaroeseTime.TryParse(to, out var toTime))
        {
            throw new McpException($"Could not read 'to' ({to}). Use a date like 2026-10-11 or a date-time like 2026-10-11T06:00.");
        }
        if (fromTime >= toTime)
        {
            throw new McpException($"'from' ({fromTime:yyyy-MM-dd HH:mm}) must be before 'to' ({toTime:yyyy-MM-dd HH:mm}); 'to' is not included.");
        }
        var maxRange = MaxRange(granularity);
        if (toTime - fromTime > maxRange)
        {
            throw new McpException(
                $"Granularity '{granularity.ToString().ToLowerInvariant()}' is limited to {maxRange.TotalDays} days per call. " +
                "Use a shorter period or a coarser granularity ('day' or 'month').");
        }

        return (fromTime, toTime);
    }

    // Matches each requested item against meter ID, nickname, meter name or address (case-insensitive;
    // address and nickname may be partial). No items means all meters that haven't been removed.
    public static IReadOnlyList<MeterSummary> ResolveMeters(IReadOnlyList<MeterSummary> available, string[]? requested)
    {
        var items = (requested ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).ToList();
        if (items.Count == 0)
        {
            var active = available.Where(m => !m.Removed).ToList();
            return active.Count > 0 ? active : available;
        }

        var selected = new List<MeterSummary>();
        foreach (var item in items)
        {
            var matches = available.Where(m => Matches(m, item)).ToList();
            if (matches.Count == 0)
            {
                throw new McpException(
                    $"No meter matches '{item}'. Available meters: " +
                    string.Join("; ", available.Select(m =>
                        $"{m.MeterId} {m.MeterName}{(m.Nickname is null ? "" : $" \"{m.Nickname}\"")} at {m.Address}")));
            }
            selected.AddRange(matches.Where(m => !selected.Contains(m)));
        }
        return selected;
    }

    private static bool Matches(MeterSummary meter, string item) =>
        meter.MeterId.ToString() == item
        || string.Equals(meter.MeterName, item, StringComparison.OrdinalIgnoreCase)
        || (meter.Nickname?.Contains(item, StringComparison.OrdinalIgnoreCase) ?? false)
        || meter.Address.Contains(item, StringComparison.OrdinalIgnoreCase);

    // One result per selected meter, in order. Meters SEV left out are reported as having no data,
    // and every total and period says how many of its expected hours have no reading, so missing or
    // partial data is never mistaken for zero or complete usage.
    // notFetched is the part of the range SevDataService didn't get to in this call; missing hours there
    // are reported as "not fetched yet" rather than as missing data.
    public static IReadOnlyList<MeterUsageResult> Summarize(
        IReadOnlyList<MeterSummary> meters, IReadOnlyList<MeterUsage> usage, DateTime from, DateTime to, DateTime localNow,
        Granularity granularity, (DateTime From, DateTime To)? notFetched = null)
    {
        var expectedHours = ExpectedHours(from, to, localNow);
        var byMeter = usage.ToDictionary(m => m.MeterId);

        return meters
            .Select(meter => Summarize(
                meter,
                byMeter.TryGetValue(meter.MeterId.ToString(), out var m) ? m.Readings : [],
                expectedHours,
                granularity,
                notFetched))
            .ToList();
    }

    private static MeterUsageResult Summarize(
        MeterSummary meter, IReadOnlyList<UsageReading> readings, IReadOnlyList<DateTime> expectedHours, Granularity granularity,
        (DateTime From, DateTime To)? notFetched)
    {
        var readingHours = readings.Select(r => r.TimeStamp).ToHashSet();
        var missingHours = expectedHours.Count(h => !readingHours.Contains(h));
        bool IsNotFetched(DateTime hour) => notFetched is { } nf && hour >= nf.From && hour < nf.To && !readingHours.Contains(hour);
        var notFetchedHours = expectedHours.Count(IsNotFetched);
        var hasReadings = readings.Count > 0;

        var notes = new List<string>();
        if (notFetchedHours > 0)
        {
            notes.Add(
                $"Data from {notFetched!.Value.From:yyyy-MM-dd} up to (not including) {notFetched.Value.To:yyyy-MM-dd} isn't fetched yet: " +
                "SEV returns at most 31 days per call and allows 10 calls per 5 minutes. Ask again in a few minutes to get the rest; " +
                "what was already fetched is kept.");
        }
        if (!hasReadings && notFetchedHours == 0)
        {
            notes.Add(expectedHours.Count == 0
                ? "This period hasn't happened yet, so there are no readings."
                : "No readings for this period: the data is missing, not zero usage.");
        }
        else if (missingHours > notFetchedHours)
        {
            notes.Add($"{missingHours - notFetchedHours} of {expectedHours.Count} hours have no reading, so the total covers only part of the period and is too low.");
        }
        var note = notes.Count > 0 ? string.Join(" ", notes) : null;

        // A period is listed if it has readings or expected hours; its value is null without readings.
        var periods = expectedHours.Select(h => PeriodKey(h, granularity))
            .Concat(readings.Select(r => PeriodKey(r.TimeStamp, granularity)))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .Select(key =>
            {
                var inPeriod = readings.Where(r => PeriodKey(r.TimeStamp, granularity) == key).ToList();
                var missingInPeriod = expectedHours.Count(h => PeriodKey(h, granularity) == key && !readingHours.Contains(h));
                var notFetchedInPeriod = expectedHours.Any(h => PeriodKey(h, granularity) == key && IsNotFetched(h));
                return new UsagePeriod(
                    key,
                    inPeriod.Count > 0 ? Math.Round(inPeriod.Sum(r => r.Value), 3) : null,
                    missingInPeriod > 0 ? missingInPeriod : null,
                    notFetchedInPeriod ? true : null);
            })
            .ToList();

        return new MeterUsageResult(
            meter.MeterId,
            meter.MeterName,
            readings.FirstOrDefault()?.Unit ?? "kWh",
            hasReadings ? Math.Round(readings.Sum(r => r.Value), 3) : null,
            expectedHours.Count,
            missingHours,
            note,
            granularity.ToString().ToLowerInvariant(),
            periods);
    }

    // ISO-style keys sort chronologically as strings.
    private static string PeriodKey(DateTime time, Granularity granularity) => granularity switch
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
        var currentHour = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, 0, 0);
        var end = to < currentHour ? to : currentHour;
        var hours = new List<DateTime>();
        for (var hour = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0); hour < end; hour = hour.AddHours(1))
        {
            if (hour >= from && !SevTime.Zone.IsInvalidTime(hour))
            {
                hours.Add(hour);
            }
        }
        return hours;
    }
}

// Total is null when the meter has no readings. MissingHours counts expected hours without a reading;
// Note explains either case in words. Periods are hours, days or months depending on Granularity.
public record MeterUsageResult(
    int MeterId,
    string MeterName,
    string Unit,
    decimal? Total,
    int ExpectedHours,
    int MissingHours,
    string? Note,
    string Granularity,
    IReadOnlyList<UsagePeriod> Periods);

// Period is "2026-10-10T05:00" (hour start), "2026-10-10" (day) or "2026-10" (month), in Faroese local time.
// Value is null when the period has no readings; MissingHours is null when none are missing.
// NotFetched is true when some of the missing hours simply haven't been fetched yet (see the meter's Note).
public record UsagePeriod(string Period, decimal? Value, int? MissingHours, bool? NotFetched = null);
