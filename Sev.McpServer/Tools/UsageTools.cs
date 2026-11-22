using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sev.Client;
using Sev.Client.Models;

namespace Sev.McpServer.Tools;

[McpServerToolType]
public class UsageTools(SevDataService sev)
{
    // The arguments get_usage, get_cost and get_co2 share, described once.
    private const string FromDescription = "Start, inclusive. A date (2026-10-10) or date-time (2026-10-10T06:00) in Faroese local time.";
    private const string ToDescription = "End, exclusive. A date (2026-10-11) or date-time in Faroese local time.";
    private const string GranularityDescription = "'hour', 'day' (default) or 'month'.";
    private const string MetersDescription =
        "Meters by ID, nickname, meter name or part of the address, as listed by list_meters. Leave out for all active meters.";

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
    public Task<IReadOnlyList<MeterSeriesResult>> GetUsageAsync(
        [Description(FromDescription)] string from,
        [Description(ToDescription)] string to,
        [Description(GranularityDescription)] string granularity = "day",
        [Description(MetersDescription)] string[]? meters = null,
        CancellationToken cancellationToken = default) =>
        GetHourlySeriesAsync(
            from, to, granularity, meters,
            async (ids, fromTime, toTime) => await sev.GetHourlyUsageAsync(ids, fromTime, toTime, cancellationToken),
            defaultUnit: "kWh", decimals: 3, cancellationToken);

    [McpServerTool(Name = "get_cost", Title = "Get estimated electricity cost", ReadOnly = true, Idempotent = true)]
    [Description(
        "Gets SEV's estimated cost of the electricity used, in DKK (unit 'DKR'), per hour, day or month for the user's meters, " +
        "with a total per meter. Each hour's cost is that hour's kWh times SEV's price per kWh for the meter's tariff. " +
        "It is an estimate of the electricity used and may differ from the bill. " +
        "Arguments, limits and missing-data reporting are the same as for get_usage: times are local Faroese time, " +
        "the range is [from, to), and long periods may come back partly fetched with a note to ask again in a few minutes. " +
        "Cost uses the same SEV call budget as get_usage (10 calls per 5 minutes), so asking for both usage and cost of a " +
        "long period takes twice the calls. Completed past days are cached. " +
        "If a total or value is null, or missingHours is above 0, tell the user that data is missing for that period; " +
        "don't report it as zero cost or as a complete total.")]
    public Task<IReadOnlyList<MeterSeriesResult>> GetCostAsync(
        [Description(FromDescription)] string from,
        [Description(ToDescription)] string to,
        [Description(GranularityDescription)] string granularity = "day",
        [Description(MetersDescription)] string[]? meters = null,
        CancellationToken cancellationToken = default) =>
        GetHourlySeriesAsync(
            from, to, granularity, meters,
            async (ids, fromTime, toTime) => await sev.GetEstimatedCostAsync(ids, fromTime, toTime, cancellationToken),
            defaultUnit: "DKR", decimals: 2, cancellationToken);

    [McpServerTool(Name = "get_co2", Title = "Get estimated CO2 emissions", ReadOnly = true, Idempotent = true)]
    [Description(
        "Gets SEV's estimated CO2 emissions in kg (unit 'Kg CO2') for the electricity used, per hour, day or month for the " +
        "user's meters, with a total per meter. SEV estimates each hour as (1 - that hour's green share of production) * kWh * " +
        "0.641 kg CO2/kWh, so hours when the grid runs on wind, water or sun count less. It is an indicative estimate. " +
        "Arguments, limits and missing-data reporting are the same as for get_usage: times are local Faroese time, " +
        "the range is [from, to), and long periods may come back partly fetched with a note to ask again in a few minutes. " +
        "CO2 uses the same SEV call budget as get_usage and get_cost (10 calls per 5 minutes). Completed past days are cached. " +
        "If a total or value is null, or missingHours is above 0, tell the user that data is missing for that period; " +
        "don't report it as zero emissions or as a complete total.")]
    public Task<IReadOnlyList<MeterSeriesResult>> GetCo2Async(
        [Description(FromDescription)] string from,
        [Description(ToDescription)] string to,
        [Description(GranularityDescription)] string granularity = "day",
        [Description(MetersDescription)] string[]? meters = null,
        CancellationToken cancellationToken = default) =>
        GetHourlySeriesAsync(
            from, to, granularity, meters,
            async (ids, fromTime, toTime) => await sev.GetEstimatedCo2Async(ids, fromTime, toTime, cancellationToken),
            defaultUnit: "Kg CO2", decimals: 2, cancellationToken);

    // What get_usage, get_cost and get_co2 share: check the arguments, pick the meters, fetch the hourly series
    // with `fetch`, and summarize it. defaultUnit is used when no reading says its unit; decimals is the rounding.
    private Task<IReadOnlyList<MeterSeriesResult>> GetHourlySeriesAsync(
        string from,
        string to,
        string granularity,
        string[]? meters,
        Func<IReadOnlyList<int>, DateTime, DateTime, Task<IHourlyResult>> fetch,
        string defaultUnit,
        int decimals,
        CancellationToken cancellationToken)
    {
        var bucket = ParseGranularity(granularity);
        var (fromTime, toTime) = ParseRange(from, to, bucket);
        return SevToolErrors.CallAsync(async () =>
        {
            var available = SevToolErrors.ThrowIfEmpty(MeterTools.Summarize(await sev.GetAvailableMetersAsync(cancellationToken)));
            var selected = ResolveMeters(available, meters);

            var result = SevToolErrors.ThrowIfEmptyAnswer(await fetch(selected.Select(m => m.MeterId).ToList(), fromTime, toTime));
            return HourlySummary.Summarize(
                selected, result.Meters, fromTime, toTime, SevTime.LocalNow(TimeProvider.System), bucket, result.NotFetched,
                defaultUnit, decimals);
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
}
