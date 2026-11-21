using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sev.Client;
using Sev.Client.Models;

namespace Sev.McpServer.Tools;

[McpServerToolType]
public class UsageTools(SevDataService sev)
{
    // Hourly readings for longer periods would flood Claude's context (about 720 readings per meter per month).
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(31);

    [McpServerTool(Name = "get_hourly_usage", Title = "Get hourly electricity usage", ReadOnly = true, Idempotent = true)]
    [Description(
        "Gets hourly electricity consumption in kWh for one or more meters, with a total per meter. " +
        "Times are local Faroese time and the range is [from, to): 'to' is not included, so yesterday is " +
        "from yesterday 00:00 to today 00:00. At most 31 days per call. " +
        "The SEV API allows only 10 calls per 5 minutes, so request all meters and the whole period in one call " +
        "instead of several small ones. Completed past days are cached, so asking about them again is cheap. " +
        "If a meter's total is null or missingHours is above 0, tell the user that data is missing for that period; " +
        "don't report it as zero usage or as a complete total.")]
    public Task<IReadOnlyList<MeterUsageResult>> GetHourlyUsageAsync(
        [Description("Meter IDs from list_meters.")] int[] meterIds,
        [Description("Start, inclusive. A date (2026-10-10) or date-time (2026-10-10T06:00) in Faroese local time.")] string from,
        [Description("End, exclusive. A date (2026-10-11) or date-time in Faroese local time.")] string to,
        CancellationToken cancellationToken)
    {
        var (fromTime, toTime) = ParseRange(meterIds, from, to);
        return SevToolErrors.CallAsync(async () =>
        {
            // Check SEV's own answer for emptiness (likely throttling) before meters without data are added.
            var usage = SevToolErrors.ThrowIfEmpty(await sev.GetHourlyUsageAsync(meterIds, fromTime, toTime, cancellationToken));
            return Summarize(meterIds, usage, fromTime, toTime, SevTime.LocalNow(TimeProvider.System));
        });
    }

    // Validates the arguments; McpException messages are shown to Claude so it can correct the call.
    public static (DateTime From, DateTime To) ParseRange(int[] meterIds, string from, string to)
    {
        if (meterIds is null || meterIds.Length == 0)
        {
            throw new McpException("Give at least one meter ID. Use list_meters to find them.");
        }
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
        if (toTime - fromTime > MaxRange)
        {
            throw new McpException($"Hourly data is limited to {MaxRange.TotalDays} days per call. Use a shorter period.");
        }

        return (fromTime, toTime);
    }

    // One result per requested meter, in the requested order. Meters SEV left out are reported as
    // having no data, and every result says how many of the expected hours have no reading, so a
    // missing or partial period is never mistaken for zero or complete usage.
    public static IReadOnlyList<MeterUsageResult> Summarize(
        IReadOnlyList<int> meterIds, IReadOnlyList<MeterUsage> usage, DateTime from, DateTime to, DateTime localNow)
    {
        var expectedHours = ExpectedHours(from, to, localNow);
        var byMeter = usage.ToDictionary(m => m.MeterId);

        return meterIds
            .Distinct()
            .Select(id => byMeter.TryGetValue(id.ToString(), out var m) ? m : new MeterUsage(id.ToString(), []))
            .Select(m => Summarize(m, expectedHours))
            .ToList();
    }

    private static MeterUsageResult Summarize(MeterUsage meter, IReadOnlyList<DateTime> expectedHours)
    {
        var readingHours = meter.Readings.Select(r => r.TimeStamp).ToHashSet();
        var missingHours = expectedHours.Count(h => !readingHours.Contains(h));
        var hasReadings = meter.Readings.Count > 0;

        string? note = null;
        if (!hasReadings)
        {
            note = expectedHours.Count == 0
                ? "This period hasn't happened yet, so there are no readings."
                : "No readings for this period: the data is missing, not zero usage.";
        }
        else if (missingHours > 0)
        {
            note = $"{missingHours} of {expectedHours.Count} hours have no reading, so the total covers only part of the period and is too low.";
        }

        return new MeterUsageResult(
            meter.MeterId,
            meter.Readings.FirstOrDefault()?.Unit ?? "kWh",
            hasReadings ? Math.Round(meter.Readings.Sum(r => r.Value), 3) : null,
            expectedHours.Count,
            missingHours,
            note,
            meter.Readings
                .Select(r => new HourlyReading(r.TimeStamp.ToString("yyyy-MM-dd'T'HH:mm"), Math.Round(r.Value, 3)))
                .ToList());
    }

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
// Note explains either case in words.
public record MeterUsageResult(
    string MeterId,
    string Unit,
    decimal? Total,
    int ExpectedHours,
    int MissingHours,
    string? Note,
    IReadOnlyList<HourlyReading> Readings);

// Hour is the start of the hour in Faroese local time.
public record HourlyReading(string Hour, decimal Value);
