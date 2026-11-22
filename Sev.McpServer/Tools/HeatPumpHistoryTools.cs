using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sev.Client;
using Sev.McpServer.HeatPump;

namespace Sev.McpServer.Tools;

// Past heat pump readings from the server's own hourly log (HeatPumpLog), since myUplink doesn't give apps its
// history. Registered together with HeatPumpTools, only when myUplink is configured.
[McpServerToolType]
public class HeatPumpHistoryTools(HeatPumpLog log, TimeProvider time)
{
    [McpServerTool(Name = "get_heatpump_history", Title = "Get heat pump history", ReadOnly = true, Idempotent = true)]
    [Description(
        "Gets past heat pump readings (temperatures, compressor speed, run-hour counters, ...) per hour, day or month, " +
        "from the server's own log: it records the heat pump's measurements once an hour, so there is no data from before " +
        "logging started (loggedFrom). Times are local Faroese time and the range is [from, to), like get_usage, so " +
        "the two can be compared hour by hour, e.g. electricity use against outdoor temperature. " +
        "Per hour you get value; per day or month the average, min and max of the logged hours. For counters such as " +
        "operation time, max - min is the increase in the period. Leave out 'parameters' for outdoor temperature only; " +
        "otherwise give names (or parts of names) as shown by get_heatpump_status, or 'all'. Settings and status codes " +
        "aren't logged. If missingHours is above 0, some hours have no sample (server down or myUplink not answering): " +
        "say so rather than presenting the result as complete.")]
    public IReadOnlyList<HeatPumpHistoryResult> GetHeatPumpHistory(
        [Description("Start, inclusive. A date (2026-10-10) or date-time (2026-10-10T06:00) in Faroese local time.")] string from,
        [Description("End, exclusive. A date (2026-10-11) or date-time in Faroese local time.")] string to,
        [Description("'hour', 'day' (default) or 'month'.")] string granularity = "day",
        [Description("Reading names or parts of names, e.g. 'Outdoor Temperature', 'Primary flow temp', or 'all'. Default: outdoor temperature.")]
        string[]? parameters = null)
    {
        var bucket = Periods.ParseGranularity(granularity);
        var (fromTime, toTime) = Periods.ParseRange(from, to, bucket);
        return Summarize(log.Snapshot(), fromTime, toTime, SevTime.LocalNow(time), bucket, parameters);
    }

    // One result per logged device: the selected readings per period over [from, to). Hours without a sample
    // since logging started count as missing (never as a zero), and the note says when logging started.
    public static IReadOnlyList<HeatPumpHistoryResult> Summarize(
        IReadOnlyList<LoggedDevice> devices, DateTime from, DateTime to, DateTime localNow, Granularity granularity, string[]? parameters)
    {
        var logged = devices.Where(d => d.Samples.Count > 0).ToList();
        if (logged.Count == 0)
        {
            throw new McpException(
                "Nothing is logged yet. The server records the heat pump's readings once an hour from when it starts with " +
                "myUplink configured; ask again after the next full hour.");
        }
        return logged.Select(device => SummarizeDevice(device, from, to, localNow, granularity, parameters)).ToList();
    }

    private static HeatPumpHistoryResult SummarizeDevice(
        LoggedDevice device, DateTime from, DateTime to, DateTime localNow, Granularity granularity, string[]? parameters)
    {
        var selected = SelectParameters(device.Parameters, parameters);
        var loggedFrom = device.Samples.Min(s => s.Hour);
        var start = from > loggedFrom ? from : loggedFrom;
        // The current hour counts once its sample is taken, so "now" is the end of this hour.
        var expected = start < to ? Periods.ExpectedHours(start, to, localNow.AddHours(1)) : [];
        var samples = device.Samples.Where(s => s.Hour >= from && s.Hour < to).ToDictionary(s => s.Hour);

        var expectedByPeriod = expected.ToLookup(h => Periods.Key(h, granularity));
        var series = selected.Select(parameter => SummarizeSeries(parameter, samples, expectedByPeriod, granularity)).ToList();

        var notes = new List<string>();
        if (from < loggedFrom)
        {
            notes.Add($"Logging started {loggedFrom:yyyy-MM-dd HH:mm}; there are no readings before that.");
        }
        var missingTotal = expected.Count(h => !samples.ContainsKey(h));
        if (missingTotal > 0)
        {
            notes.Add($"{missingTotal} of {expected.Count} hours since then have no sample (server down or myUplink not answering).");
        }

        return new HeatPumpHistoryResult(
            device.Product,
            loggedFrom.ToString("yyyy-MM-dd'T'HH:mm"),
            granularity.ToString().ToLowerInvariant(),
            notes.Count > 0 ? string.Join(" ", notes) : null,
            series);
    }

    // One reading per period: its value (per hour) or average, min and max (per day or month), and how many
    // expected hours have no sample with this reading.
    private static HeatPumpSeries SummarizeSeries(
        LoggedParameter parameter,
        Dictionary<DateTime, LoggedSample> samples,
        ILookup<string, DateTime> expectedByPeriod,
        Granularity granularity)
    {
        bool HasValue(DateTime hour) => samples.TryGetValue(hour, out var s) && s.Values.ContainsKey(parameter.Id);

        var valuesByPeriod = samples.Values
            .Where(s => s.Values.ContainsKey(parameter.Id))
            .ToLookup(s => Periods.Key(s.Hour, granularity), s => s.Values[parameter.Id]);

        var periods = expectedByPeriod.Select(g => g.Key)
            .Union(valuesByPeriod.Select(g => g.Key))
            .Order(StringComparer.Ordinal)
            .Select(key =>
            {
                var values = valuesByPeriod[key].ToList();
                var missing = expectedByPeriod[key].Count(h => !HasValue(h));
                var missingHours = missing > 0 ? missing : (int?)null;
                return values.Count == 0 ? new HeatPumpPeriod(key, null, null, null, null, missingHours)
                    : granularity == Granularity.Hour ? new HeatPumpPeriod(key, Round(values[0]), null, null, null, missingHours)
                    : new HeatPumpPeriod(key, null, Round(values.Average()), Round(values.Min()), Round(values.Max()), missingHours);
            })
            .ToList();
        return new HeatPumpSeries(parameter.Name, parameter.Unit, parameter.Category, periods);
    }

    // No names: outdoor temperature (every reading with "outdoor" in its name), or everything if there's none.
    // "all": everything. Otherwise each name must match an ID, a whole name or part of one (case-insensitive).
    private static List<LoggedParameter> SelectParameters(IReadOnlyList<LoggedParameter> available, string[]? requested)
    {
        var ordered = available.OrderBy(p => p.Category, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
        var items = (requested ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).ToList();
        if (items.Count == 0)
        {
            var outdoor = ordered.Where(p => p.Name.Contains("outdoor", StringComparison.OrdinalIgnoreCase)).ToList();
            return outdoor.Count > 0 ? outdoor : ordered;
        }
        if (items.Any(i => i.Equals("all", StringComparison.OrdinalIgnoreCase)))
        {
            return ordered;
        }

        var selected = new List<LoggedParameter>();
        foreach (var item in items)
        {
            var matches = ordered.Where(p => p.Id == item || p.Name.Contains(item, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                throw new McpException(
                    $"No logged reading matches '{item}'. Logged readings: " + string.Join("; ", ordered.Select(p => p.Name).Distinct()));
            }
            selected.AddRange(matches.Where(m => !selected.Contains(m)));
        }
        return selected;
    }

    private static double Round(double value) => Math.Round(value, 2);
}

// One device's logged readings. LoggedFrom is the first logged hour (Faroese local time).
public record HeatPumpHistoryResult(
    string? Product,
    string LoggedFrom,
    string Granularity,
    string? Note,
    IReadOnlyList<HeatPumpSeries> Series);

public record HeatPumpSeries(string Name, string? Unit, string? Category, IReadOnlyList<HeatPumpPeriod> Periods);

// Per hour: Value. Per day or month: Average, Min and Max of the logged hours. All null when the period has no
// sample; MissingHours counts hours since logging started without one.
public record HeatPumpPeriod(string Period, double? Value, double? Average, double? Min, double? Max, int? MissingHours);
