using Sev.Client.Models;

namespace Sev.Client;

// Same data as SevClient, but served from SevCache where possible, to save SEV's 10 calls per 5 minutes.
// - The meter list is kept for SevCache.MeterListLifetime.
// - Usage is kept per meter per day, but only for days before today (Faroese time) that are complete,
//   or old enough that SEV won't add to them (SevCache.DaysUntilFinal), gaps included.
// - Empty answers are never cached: SEV returns them when the rate limit is exceeded.
// Only what can exist is fetched: nothing from the current hour on, and nothing outside a meter's
// install and removal dates (from the cached meter list). Whatever isn't cached is fetched in pieces of
// at most MaxDaysPerRequest days (SEV silently cuts off longer requests), newest first, and at most
// MaxRequestsPerCall pieces per call so one question can't use up SEV's 10 calls per 5 minutes. What
// didn't fit is reported in UsageResult.NotFetched; since fetched days are cached, asking again later fills it in.
public class SevDataService(SevClient sev, SevCache cache)
{
    // SEV returns at most 31 days per hourly_kwh_usage call, without saying so (observed 2026-11-21:
    // a one-year request returned 2025-11-21 to 2025-12-21 only).
    public const int MaxDaysPerRequest = 31;

    // Leaves room for login, the meter list and other questions within SEV's 10 calls per 5 minutes.
    public const int MaxRequestsPerCall = 6;

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

    public async Task<UsageResult> GetHourlyUsageAsync(
        IReadOnlyList<int> meterIds, DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        SevClient.ValidateRange(from, to);
        var meters = meterIds.Distinct().ToList();

        await cache.Lock.WaitAsync(cancellationToken);
        try
        {
            var now = SevTime.LocalNow(cache.Time);
            var today = DateOnly.FromDateTime(now);
            var todayStart = today.ToDateTime(TimeOnly.MinValue);
            var days = DaysIn(from, to);

            // A reading is stamped with the start of its hour and exists only once the hour has ended, so
            // nothing from the current hour on is fetched. A range in the future therefore costs no call.
            var currentHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
            var fetchEnd = to < currentHour ? to : currentHour;
            var knownMeters = (cache.Meters ?? [])
                .SelectMany(c => c.Installations)
                .SelectMany(i => i.Meters)
                .ToLookup(m => m.MeterId);

            // Today is never cached, so it is always missing while it lies in the range.
            var fetchableDays = DaysIn(from, fetchEnd);
            var missing = (
                from meter in meters
                from day in fetchableDays
                where !cache.UsageDays.ContainsKey((meter, day)) && IsInstalled(knownMeters[meter], day)
                select (Meter: meter, Day: day)).ToList();

            var fetched = new Dictionary<int, List<UsageReading>>();
            (DateTime From, DateTime To)? notFetched = null;
            var emptyAnswer = false;
            if (missing.Count > 0)
            {
                // Missing past days are fetched as whole days so they can be cached; today up to the current hour.
                var first = missing.Min(m => m.Day);
                var last = missing.Max(m => m.Day);
                var fetchFrom = first == today ? Max(from, todayStart) : first.ToDateTime(TimeOnly.MinValue);
                var fetchTo = last == today ? fetchEnd : last.AddDays(1).ToDateTime(TimeOnly.MinValue);
                var fetchMeters = missing.Select(m => m.Meter).Distinct().ToList();

                var pieces = PiecesNewestFirst(fetchFrom, fetchTo);
                var anyAnswer = false;
                for (var i = 0; i < pieces.Count; i++)
                {
                    var (pieceFrom, pieceTo) = pieces[i];
                    if (i >= MaxRequestsPerCall)
                    {
                        notFetched = (fetchFrom, pieceTo);
                        break;
                    }

                    IReadOnlyList<MeterUsage> usage;
                    try
                    {
                        usage = await sev.GetHourlyUsageAsync(fetchMeters, pieceFrom, pieceTo, cancellationToken);
                    }
                    catch (SevRateLimitExceededException) when (anyAnswer)
                    {
                        // Keep what was fetched; the rest is reported as not fetched.
                        notFetched = (fetchFrom, pieceTo);
                        break;
                    }

                    if (usage.Count == 0)
                    {
                        // Most likely the rate limit. Cached days are still returned and the rest is reported
                        // as not fetched; EmptyAnswer tells the caller when SEV gave nothing at all.
                        emptyAnswer = !anyAnswer;
                        notFetched = (fetchFrom, pieceTo);
                        break;
                    }

                    anyAnswer = true;
                    foreach (var meterUsage in usage)
                    {
                        if (int.TryParse(meterUsage.MeterId, out var meter))
                        {
                            if (!fetched.TryGetValue(meter, out var list))
                            {
                                fetched[meter] = list = [];
                            }
                            list.AddRange(meterUsage.Readings);
                        }
                    }
                    StoreDays(fetchMeters, usage, today, pieceFrom, pieceTo);
                }
            }

            var result = meters
                .Select(meter => (Meter: meter, Readings: Collect(meter, days, fetched, from, to)))
                .Where(m => m.Readings is not null)
                .Select(m => new MeterUsage(m.Meter.ToString(), m.Readings!))
                .ToList();
            return new UsageResult(result, notFetched, emptyAnswer);
        }
        finally
        {
            cache.Lock.Release();
        }
    }

    // A meter the cached meter list doesn't know is assumed to be installed. If the list holds the
    // meter more than once (several installations), it counts as installed when any of them says so.
    private static bool IsInstalled(IEnumerable<Meter> records, DateOnly day) =>
        !records.Any()
        || records.Any(m => (m.InstallDate is null || day >= m.InstallDate) && (m.RemovalDate is null || day <= m.RemovalDate));

    // Caches the past days that lie wholly inside the piece, for every meter that was asked for: complete
    // days, and incomplete ones (even without any readings) once they are old enough to be final. Only
    // called for a non-empty answer, so a meter or day missing from it really has no data at SEV.
    private void StoreDays(
        IReadOnlyList<int> meters, IReadOnlyList<MeterUsage> usage, DateOnly today, DateTime pieceFrom, DateTime pieceTo)
    {
        var byMeterAndDay = usage
            .SelectMany(m => m.Readings, (m, r) => (MeterId: m.MeterId, Reading: r))
            .ToLookup(x => (x.MeterId, DateOnly.FromDateTime(x.Reading.TimeStamp)), x => x.Reading);
        var finalBefore = today.AddDays(-SevCache.DaysUntilFinal + 1);

        for (var day = DateOnly.FromDateTime(pieceFrom); day < today; day = day.AddDays(1))
        {
            var dayStart = day.ToDateTime(TimeOnly.MinValue);
            if (dayStart < pieceFrom)
            {
                continue;
            }
            if (day.AddDays(1).ToDateTime(TimeOnly.MinValue) > pieceTo)
            {
                break;
            }

            foreach (var meter in meters)
            {
                var readings = byMeterAndDay[(meter.ToString(), day)].OrderBy(r => r.TimeStamp).ToList();
                if (readings.Count >= SevCache.MinReadingsForCompleteDay || day < finalBefore)
                {
                    cache.UsageDays[(meter, day)] = readings;
                }
            }
        }
    }

    // Readings for one meter within [from, to): cached days first, fetched data for the rest.
    // Null when there is no data at all for the meter (neither cached nor in SEV's answer).
    private List<UsageReading>? Collect(
        int meter, IReadOnlyList<DateOnly> days, Dictionary<int, List<UsageReading>> fetched, DateTime from, DateTime to)
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

    // The days that [from, to) touches; none when the range is empty.
    private static List<DateOnly> DaysIn(DateTime from, DateTime to)
    {
        var days = new List<DateOnly>();
        if (from >= to)
        {
            return days;
        }
        for (var day = DateOnly.FromDateTime(from); day <= DateOnly.FromDateTime(to.AddTicks(-1)); day = day.AddDays(1))
        {
            days.Add(day);
        }
        return days;
    }

    // Splits [from, to) into pieces of at most MaxDaysPerRequest calendar days, newest first. Boundaries
    // fall on midnight, so every whole day lies inside one piece and can be cached.
    private static List<(DateTime From, DateTime To)> PiecesNewestFirst(DateTime from, DateTime to)
    {
        var pieces = new List<(DateTime From, DateTime To)>();
        for (var end = to; end > from;)
        {
            var start = end.AddTicks(-1).Date.AddDays(-(MaxDaysPerRequest - 1));
            if (start < from)
            {
                start = from;
            }
            pieces.Add((start, end));
            end = start;
        }
        return pieces;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}

// Meters holds the readings within the requested range: SEV's answer plus cached days. NotFetched is the
// oldest part of the range that wasn't fetched in this call (SEV's per-call limits or an empty answer);
// null when everything was fetched. EmptyAnswer is true when SEV answered the first request with an empty
// list, which usually means throttling; Meters then holds only cached days, if any.
public record UsageResult(
    IReadOnlyList<MeterUsage> Meters, (DateTime From, DateTime To)? NotFetched, bool EmptyAnswer = false);
