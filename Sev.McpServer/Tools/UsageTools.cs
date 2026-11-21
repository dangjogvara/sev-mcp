using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sev.Client;
using Sev.Client.Models;

namespace Sev.McpServer.Tools;

[McpServerToolType]
public class UsageTools(SevClient sev)
{
    // Hourly readings for longer periods would flood Claude's context (about 720 readings per meter per month).
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(31);

    [McpServerTool(Name = "get_hourly_usage", Title = "Get hourly electricity usage", ReadOnly = true, Idempotent = true)]
    [Description(
        "Gets hourly electricity consumption in kWh for one or more meters, with a total per meter. " +
        "Times are local Faroese time and the range is [from, to): 'to' is not included, so yesterday is " +
        "from yesterday 00:00 to today 00:00. At most 31 days per call. " +
        "The SEV API allows only 10 calls per 5 minutes, so request all meters and the whole period in one call " +
        "instead of several small ones.")]
    public Task<IReadOnlyList<MeterUsageResult>> GetHourlyUsageAsync(
        [Description("Meter IDs from list_meters.")] int[] meterIds,
        [Description("Start, inclusive. A date (2026-10-10) or date-time (2026-10-10T06:00) in Faroese local time.")] string from,
        [Description("End, exclusive. A date (2026-10-11) or date-time in Faroese local time.")] string to,
        CancellationToken cancellationToken)
    {
        var (fromTime, toTime) = ParseRange(meterIds, from, to);
        return SevToolErrors.CallAsync(async () =>
            SevToolErrors.ThrowIfEmpty(Summarize(await sev.GetHourlyUsageAsync(meterIds, fromTime, toTime, cancellationToken))));
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

    public static IReadOnlyList<MeterUsageResult> Summarize(IReadOnlyList<MeterUsage> usage) =>
        usage
            .Select(m => new MeterUsageResult(
                m.MeterId,
                m.Readings.FirstOrDefault()?.Unit ?? "kWh",
                Math.Round(m.Readings.Sum(r => r.Value), 3),
                m.Readings
                    .Select(r => new HourlyReading(r.TimeStamp.ToString("yyyy-MM-dd'T'HH:mm"), Math.Round(r.Value, 3)))
                    .ToList()))
            .ToList();
}

public record MeterUsageResult(string MeterId, string Unit, decimal Total, IReadOnlyList<HourlyReading> Readings);

// Hour is the start of the hour in Faroese local time.
public record HourlyReading(string Hour, decimal Value);
