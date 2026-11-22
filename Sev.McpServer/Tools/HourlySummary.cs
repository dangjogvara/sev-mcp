using Sev.Client.Models;

namespace Sev.McpServer.Tools;

// Turns hourly readings (usage, cost or CO2) into the totals and periods the tools return, and works out
// which hours should have a reading, so missing data is reported as missing and never as zero.
public static class HourlySummary
{
    // One result per selected meter, in order. Meters SEV left out are reported as having no data,
    // and every total and period says how many of its expected hours have no reading, so missing or
    // partial data is never mistaken for zero or complete usage.
    // Hours outside a meter's installation aren't expected, so a meter installed partway through the
    // range isn't reported as missing data before it was there.
    // notFetched is the part of the range SevDataService didn't get to in this call; missing hours there
    // are reported as "not fetched yet" rather than as missing data.
    // Works for any hourly series (kWh usage, DKK cost, kg CO2): defaultUnit is used when there are no readings to
    // take the unit from, and values are rounded to `decimals` places.
    public static IReadOnlyList<MeterSeriesResult> Summarize(
        IReadOnlyList<MeterSummary> meters, IReadOnlyList<IMeterReadings> usage, DateTime from, DateTime to, DateTime localNow,
        Granularity granularity, (DateTime From, DateTime To)? notFetched = null, string defaultUnit = "kWh", int decimals = 3)
    {
        var rangeHours = Periods.ExpectedHours(from, to, localNow);
        var byMeter = usage.ToDictionary(m => m.MeterId);

        return meters
            .Select(meter => Summarize(
                meter,
                byMeter.TryGetValue(meter.MeterId.ToString(), out var m) ? m.Readings : [],
                rangeHours,
                granularity,
                notFetched,
                defaultUnit,
                decimals))
            .ToList();
    }

    // The hours in rangeHours that fall within the meter's installation. The install and removal dates
    // have no time of day, so on those days the meter is taken to start at its first reading and to end
    // after its last one; without a reading that day, the whole day is expected.
    public static IReadOnlyList<DateTime> ExpectedHoursFor(
        MeterSummary meter, IReadOnlyList<DateTime> rangeHours, IReadOnlyList<IHourlyReading> readings)
    {
        IEnumerable<DateTime> ReadingsOn(DateOnly day) =>
            readings.Select(r => r.TimeStamp).Where(t => DateOnly.FromDateTime(t) == day);

        var start = DateTime.MinValue;
        if (meter.InstallDate is { } installed)
        {
            start = ReadingsOn(installed).DefaultIfEmpty(installed.ToDateTime(TimeOnly.MinValue)).Min();
        }

        var end = DateTime.MaxValue;
        if (meter.RemovalDate is { } removed)
        {
            var onRemovalDay = ReadingsOn(removed).ToList();
            end = onRemovalDay.Count > 0 ? onRemovalDay.Max().AddHours(1) : removed.AddDays(1).ToDateTime(TimeOnly.MinValue);
        }

        return rangeHours.Where(h => h >= start && h < end).ToList();
    }

    private static MeterSeriesResult Summarize(
        MeterSummary meter, IReadOnlyList<IHourlyReading> readings, IReadOnlyList<DateTime> rangeHours, Granularity granularity,
        (DateTime From, DateTime To)? notFetched, string defaultUnit, int decimals)
    {
        var expectedHours = ExpectedHoursFor(meter, rangeHours, readings);
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
            notes.Add(
                rangeHours.Count == 0 ? "This period hasn't happened yet, so there are no readings."
                : expectedHours.Count == 0 ? $"The meter wasn't installed during this period ({Installation(meter)}), so there are no readings."
                : "No readings for this period: the data is missing, not zero usage.");
        }
        else if (missingHours > notFetchedHours)
        {
            notes.Add($"{missingHours - notFetchedHours} of {expectedHours.Count} hours have no reading, so the total covers only part of the period and is too low.");
        }
        var note = notes.Count > 0 ? string.Join(" ", notes) : null;

        // A period is listed if it has readings or expected hours; its value is null without readings.
        // Readings and expected hours are grouped by period once, then each period is looked up.
        var readingsByPeriod = readings.ToLookup(r => Periods.Key(r.TimeStamp, granularity));
        var expectedByPeriod = expectedHours.ToLookup(h => Periods.Key(h, granularity));
        var periods = expectedByPeriod.Select(g => g.Key)
            .Union(readingsByPeriod.Select(g => g.Key))
            .Order(StringComparer.Ordinal)
            .Select(key =>
            {
                var inPeriod = readingsByPeriod[key].ToList();
                var missingInPeriod = expectedByPeriod[key].Count(h => !readingHours.Contains(h));
                var notFetchedInPeriod = expectedByPeriod[key].Any(IsNotFetched);
                return new SeriesPeriod(
                    key,
                    inPeriod.Count > 0 ? Math.Round(inPeriod.Sum(r => r.Value), decimals) : null,
                    missingInPeriod > 0 ? missingInPeriod : null,
                    notFetchedInPeriod ? true : null);
            })
            .ToList();

        return new MeterSeriesResult(
            meter.MeterId,
            meter.MeterName,
            readings.FirstOrDefault()?.Unit ?? defaultUnit,
            hasReadings ? Math.Round(readings.Sum(r => r.Value), decimals) : null,
            expectedHours.Count,
            missingHours,
            note,
            granularity.ToString().ToLowerInvariant(),
            periods);
    }

    private static string Installation(MeterSummary meter) =>
        (meter.InstallDate, meter.RemovalDate) switch
        {
            ({ } installed, { } removed) => $"installed {installed:yyyy-MM-dd}, removed {removed:yyyy-MM-dd}",
            ({ } installed, null) => $"installed {installed:yyyy-MM-dd}",
            (null, { } removed) => $"removed {removed:yyyy-MM-dd}",
            _ => "no install date",
        };
}

// One meter's summary of an hourly series (kWh, DKK or kg CO2, see Unit). Total is null when the meter has
// no readings. MissingHours counts expected hours without a reading; Note explains either case in words.
// Periods are hours, days or months depending on Granularity.
public record MeterSeriesResult(
    int MeterId,
    string MeterName,
    string Unit,
    decimal? Total,
    int ExpectedHours,
    int MissingHours,
    string? Note,
    string Granularity,
    IReadOnlyList<SeriesPeriod> Periods);

// Period is "2026-10-10T05:00" (hour start), "2026-10-10" (day) or "2026-10" (month), in Faroese local time.
// Value is null when the period has no readings; MissingHours is null when none are missing.
// NotFetched is true when some of the missing hours simply haven't been fetched yet (see the meter's Note).
public record SeriesPeriod(string Period, decimal? Value, int? MissingHours, bool? NotFetched = null);
