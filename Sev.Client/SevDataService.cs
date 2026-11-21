using Sev.Client.Models;

namespace Sev.Client;

// Same data as SevClient, but served from SevCache where possible, to save SEV's 10 calls per 5 minutes.
// - The meter list is kept for SevCache.MeterListLifetime.
// - Usage is kept per meter per day, but only for days before today (Faroese time) that are complete.
// - Empty answers are never cached: SEV returns them when the rate limit is exceeded.
// Whatever isn't cached is fetched in a single SevClient call.
public class SevDataService(SevClient sev, SevCache cache)
{
    public async Task<IReadOnlyList<Customer>> GetAvailableMetersAsync(CancellationToken cancellationToken = default)
    {
        await cache.Lock.WaitAsync(cancellationToken);
        try
        {
            var now = cache.Time.GetUtcNow();
            if (cache.Meters is not null && now - cache.MetersFetchedAt < SevCache.MeterListLifetime)
            {
                return cache.Meters;
            }

            var customers = await sev.GetAvailableMetersAsync(cancellationToken);
            if (customers.Count > 0)
            {
                cache.Meters = customers;
                cache.MetersFetchedAt = now;
            }
            return customers;
        }
        finally
        {
            cache.Lock.Release();
        }
    }

    public async Task<IReadOnlyList<MeterUsage>> GetHourlyUsageAsync(
        IReadOnlyList<int> meterIds, DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        SevClient.ValidateRange(from, to);
        var meters = meterIds.Distinct().ToList();

        await cache.Lock.WaitAsync(cancellationToken);
        try
        {
            var today = DateOnly.FromDateTime(SevTime.LocalNow(cache.Time));
            var todayStart = today.ToDateTime(TimeOnly.MinValue);
            var days = DaysIn(from, to);

            var missing = (
                from meter in meters
                from day in days
                where day < today && !cache.UsageDays.ContainsKey((meter, day))
                select (Meter: meter, Day: day)).ToList();
            var includesToday = to > todayStart;

            var fetched = new Dictionary<int, IReadOnlyList<UsageReading>>();
            if (missing.Count > 0 || includesToday)
            {
                // Missing past days are fetched as whole days so they can be cached.
                var fetchFrom = missing.Count > 0 ? missing.Min(m => m.Day).ToDateTime(TimeOnly.MinValue) : Max(from, todayStart);
                var fetchTo = includesToday ? to : missing.Max(m => m.Day).AddDays(1).ToDateTime(TimeOnly.MinValue);
                var fetchMeters = includesToday ? meters : missing.Select(m => m.Meter).Distinct().ToList();

                var usage = await sev.GetHourlyUsageAsync(fetchMeters, fetchFrom, fetchTo, cancellationToken);
                if (usage.Count == 0)
                {
                    // Most likely the rate limit; let the caller see the empty answer rather than partial data.
                    return [];
                }

                foreach (var meterUsage in usage)
                {
                    if (int.TryParse(meterUsage.MeterId, out var meter))
                    {
                        fetched[meter] = meterUsage.Readings;
                        StoreCompleteDays(meter, meterUsage.Readings, today, fetchFrom, fetchTo);
                    }
                }
            }

            return meters
                .Select(meter => (Meter: meter, Readings: Collect(meter, days, fetched, from, to)))
                .Where(m => m.Readings is not null)
                .Select(m => new MeterUsage(m.Meter.ToString(), m.Readings!))
                .ToList();
        }
        finally
        {
            cache.Lock.Release();
        }
    }

    private void StoreCompleteDays(int meter, IReadOnlyList<UsageReading> readings, DateOnly today, DateTime fetchFrom, DateTime fetchTo)
    {
        foreach (var dayReadings in readings.GroupBy(r => DateOnly.FromDateTime(r.TimeStamp)))
        {
            var day = dayReadings.Key;
            var coveredByFetch = day.ToDateTime(TimeOnly.MinValue) >= fetchFrom && day.AddDays(1).ToDateTime(TimeOnly.MinValue) <= fetchTo;
            var readingsOfDay = dayReadings.OrderBy(r => r.TimeStamp).ToList();
            if (day < today && coveredByFetch && readingsOfDay.Count >= SevCache.MinReadingsForCompleteDay)
            {
                cache.UsageDays[(meter, day)] = readingsOfDay;
            }
        }
    }

    // Readings for one meter within [from, to): cached days first, fetched data for the rest.
    // Null when there is no data at all for the meter (neither cached nor in SEV's answer).
    private List<UsageReading>? Collect(
        int meter, IReadOnlyList<DateOnly> days, Dictionary<int, IReadOnlyList<UsageReading>> fetched, DateTime from, DateTime to)
    {
        var hasFetched = fetched.TryGetValue(meter, out var fetchedReadings);
        var anyCached = false;
        var readings = new List<UsageReading>();

        foreach (var day in days)
        {
            if (cache.UsageDays.TryGetValue((meter, day), out var cached))
            {
                anyCached = true;
                readings.AddRange(cached);
            }
            else if (hasFetched)
            {
                readings.AddRange(fetchedReadings!.Where(r => DateOnly.FromDateTime(r.TimeStamp) == day));
            }
        }

        return hasFetched || anyCached
            ? readings.Where(r => r.TimeStamp >= from && r.TimeStamp < to).OrderBy(r => r.TimeStamp).ToList()
            : null;
    }

    private static List<DateOnly> DaysIn(DateTime from, DateTime to)
    {
        var days = new List<DateOnly>();
        for (var day = DateOnly.FromDateTime(from); day <= DateOnly.FromDateTime(to.AddTicks(-1)); day = day.AddDays(1))
        {
            days.Add(day);
        }
        return days;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
