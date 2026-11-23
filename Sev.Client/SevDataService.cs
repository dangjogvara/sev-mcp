using Sev.Client.Models;

namespace Sev.Client;

// Same data as SevClient, but served from SevCache where possible, to save SEV's 10 calls per 5 minutes.
// - The meter list is kept for SevCache.MeterListLifetime.
// - Usage, cost and CO2 are kept per meter per day, but only for days before today (Faroese time) that are
//   complete, or old enough that SEV won't add to them (SevCache.DaysUntilFinal), gaps included.
//   Both go through GetHourlyAsync, which holds the fetching and caching logic once for any hourly series.
// - Empty answers are never cached: SEV returns them when the rate limit is exceeded.
// - With SevCacheFiles, whatever is newly cached is saved to disk before the call returns.
// Only what can exist is fetched: nothing from the current hour on, and nothing outside a meter's
// install and removal dates (from the cached meter list). Whatever isn't cached is fetched in pieces of
// at most MaxDaysPerRequest days (SEV silently cuts off longer requests), newest first, and at most
// MaxRequestsPerCall pieces per call so one question can't use up SEV's 10 calls per 5 minutes. What
// didn't fit is reported in NotFetched; since fetched days are cached, asking again later fills it in.
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
                cache.Files?.SaveMeters(customers, now);
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
        var data = await GetHourlyAsync(
            cache.UsageDays, meterIds, from, to,
            async (ids, pieceFrom, pieceTo) =>
                (await sev.GetHourlyUsageAsync(ids, pieceFrom, pieceTo, cancellationToken))
                    .Select(m => new MeterReadings<UsageReading>(m.MeterId, m.Readings)).ToList(),
            cancellationToken);

        var meters = data.Meters.Select(m => new MeterUsage(m.MeterId, m.Readings)).ToList();
        return new UsageResult(meters, data.NotFetched, data.EmptyAnswer);
    }

    public async Task<CostResult> GetEstimatedCostAsync(
        IReadOnlyList<int> meterIds, DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var data = await GetHourlyAsync(
            cache.CostDays, meterIds, from, to,
            async (ids, pieceFrom, pieceTo) =>
                (await sev.GetEstimatedCostAsync(ids, pieceFrom, pieceTo, cancellationToken))
                    .Select(m => new MeterReadings<CostReading>(m.MeterId, m.Readings)).ToList(),
            cancellationToken);

        var meters = data.Meters.Select(m => new MeterCost(m.MeterId, m.Readings)).ToList();
        return new CostResult(meters, data.NotFetched, data.EmptyAnswer);
    }

    public async Task<Co2Result> GetEstimatedCo2Async(
        IReadOnlyList<int> meterIds, DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var data = await GetHourlyAsync(
            cache.Co2Days, meterIds, from, to,
            async (ids, pieceFrom, pieceTo) =>
                (await sev.GetEstimatedCo2Async(ids, pieceFrom, pieceTo, cancellationToken))
                    .Select(m => new MeterReadings<Co2Reading>(m.MeterId, m.Readings)).ToList(),
            cancellationToken);

        var meters = data.Meters.Select(m => new MeterCo2(m.MeterId, m.Readings)).ToList();
        return new Co2Result(meters, data.NotFetched, data.EmptyAnswer);
    }

    // One meter's readings, for any kind of hourly reading: what SEV answers, and what this service returns.
    private record MeterReadings<TReading>(string MeterId, IReadOnlyList<TReading> Readings);

    private record HourlyData<TReading>(
        IReadOnlyList<MeterReadings<TReading>> Meters, (DateTime From, DateTime To)? NotFetched, bool EmptyAnswer);

    // What FetchMissingAsync got: readings per meter, the part it didn't get to, and whether SEV's first answer was empty.
    private record FetchOutcome<TReading>(
        Dictionary<int, List<TReading>> Readings, (DateTime From, DateTime To)? NotFetched, bool EmptyAnswer);

    // One request to SEV: the missing days it covers (FirstDay to LastDay, inclusive) and the times it asks for,
    // [From, To). From and To are whole days, except today, which is fetched from its start (or `from`) up to now.
    private readonly record struct Piece(DateOnly FirstDay, DateOnly LastDay, DateTime From, DateTime To);

    // Asks SEV for one piece: these meters, from one time to another.
    private delegate Task<List<MeterReadings<TReading>>> FetchPiece<TReading>(IReadOnlyList<int> meterIds, DateTime from, DateTime to);

    // The fetching and caching logic for any hourly series (usage, cost, CO2), in three steps: find what isn't
    // cached, fetch it from SEV, and combine both into the readings for [from, to). cachedDays is that series' cache.
    private async Task<HourlyData<TReading>> GetHourlyAsync<TReading>(
        Dictionary<(int MeterId, DateOnly Day), IReadOnlyList<TReading>> cachedDays,
        IReadOnlyList<int> meterIds,
        DateTime from,
        DateTime to,
        FetchPiece<TReading> fetch,
        CancellationToken cancellationToken)
        where TReading : IHourlyReading
    {
        SevClient.ValidateRange(from, to);
        var meters = meterIds.Distinct().ToList();

        await cache.Lock.WaitAsync(cancellationToken);
        try
        {
            var now = SevTime.LocalNow(cache.Time);
            var today = DateOnly.FromDateTime(now);

            // A reading is stamped with the start of its hour and exists only once the hour has ended, so
            // nothing from the current hour on is fetched. A range in the future therefore costs no call.
            var currentHour = SevTime.StartOfHour(now);
            var fetchEnd = to < currentHour ? to : currentHour;

            var missing = MissingDays(cachedDays, meters, from, fetchEnd);
            var cachedBefore = cachedDays.Count;
            FetchOutcome<TReading> outcome;
            try
            {
                outcome = missing.Count > 0
                    ? await FetchMissingAsync(cachedDays, missing, from, fetchEnd, today, fetch)
                    : new FetchOutcome<TReading>([], null, false);
            }
            finally
            {
                // Days are only ever added, so a higher count means something new to save. Also when a later
                // piece failed: the days from the pieces before it cost rate-limited calls too.
                if (cachedDays.Count > cachedBefore)
                {
                    cache.Files?.SaveDays(cachedDays);
                }
            }

            var days = DaysIn(from, to);
            var result = meters
                .Select(meter => (Meter: meter, Readings: Collect(cachedDays, meter, days, outcome.Readings, from, to)))
                .Where(m => m.Readings is not null)
                .Select(m => new MeterReadings<TReading>(m.Meter.ToString(), m.Readings!))
                .ToList();
            return new HourlyData<TReading>(result, outcome.NotFetched, outcome.EmptyAnswer);
        }
        finally
        {
            cache.Lock.Release();
        }
    }

    // The (meter, day) pairs in [from, fetchEnd) that aren't cached and fall within the meter's installation.
    // Today is never cached, so it is always missing while it lies in the range.
    private List<(int Meter, DateOnly Day)> MissingDays<TReading>(
        Dictionary<(int MeterId, DateOnly Day), IReadOnlyList<TReading>> cachedDays,
        IReadOnlyList<int> meters,
        DateTime from,
        DateTime fetchEnd)
    {
        var knownMeters = (cache.Meters ?? [])
            .SelectMany(c => c.Installations)
            .SelectMany(i => i.Meters)
            .ToLookup(m => m.MeterId);
        var fetchableDays = DaysIn(from, fetchEnd);

        return (
            from meter in meters
            from day in fetchableDays
            where !cachedDays.ContainsKey((meter, day)) && IsInstalled(knownMeters[meter], day)
            select (Meter: meter, Day: day)).ToList();
    }

    // Fetches the missing days from SEV in pieces of at most MaxDaysPerRequest days, newest first, skipping
    // stretches where every requested meter is already cached. Missing past days are fetched as whole days
    // so they can be cached; today up to the current hour. It stops after MaxRequestsPerCall pieces, at a
    // rate-limit refusal after the first piece, or at an empty answer (most likely the rate limit), and reports
    // the older part it didn't get to.
    private static async Task<FetchOutcome<TReading>> FetchMissingAsync<TReading>(
        Dictionary<(int MeterId, DateOnly Day), IReadOnlyList<TReading>> cachedDays,
        List<(int Meter, DateOnly Day)> missing,
        DateTime from,
        DateTime fetchEnd,
        DateOnly today,
        FetchPiece<TReading> fetch)
        where TReading : IHourlyReading
    {
        var missingSet = missing.ToHashSet();
        var missingDays = missing.Select(m => m.Day).Distinct().Order().ToList();
        var pieces = PiecesNewestFirst(missingDays, from, fetchEnd, today);
        var notFetchedFrom = pieces[^1].From;

        var fetched = new Dictionary<int, List<TReading>>();
        for (var i = 0; i < pieces.Count; i++)
        {
            var (firstDay, lastDay, pieceFrom, pieceTo) = pieces[i];
            // Every earlier piece got an answer: an empty one ends the loop.
            var anyAnswer = i > 0;
            if (i >= MaxRequestsPerCall)
            {
                return new FetchOutcome<TReading>(fetched, (notFetchedFrom, pieceTo), false);
            }

            var pieceMeters = missing
                .Where(m => m.Day >= firstDay && m.Day <= lastDay)
                .Select(m => m.Meter)
                .Distinct()
                .ToList();

            List<MeterReadings<TReading>> answer;
            try
            {
                answer = await fetch(pieceMeters, pieceFrom, pieceTo);
            }
            catch (SevRateLimitExceededException) when (anyAnswer)
            {
                // Keep what was fetched; the rest is reported as not fetched.
                return new FetchOutcome<TReading>(fetched, (notFetchedFrom, pieceTo), false);
            }

            if (answer.Count == 0)
            {
                // Most likely the rate limit. Cached days are still returned and the rest is reported as not
                // fetched; EmptyAnswer tells the caller when SEV gave nothing at all.
                return new FetchOutcome<TReading>(fetched, (notFetchedFrom, pieceTo), EmptyAnswer: !anyAnswer);
            }

            foreach (var meterReadings in answer)
            {
                if (int.TryParse(meterReadings.MeterId, out var meter))
                {
                    if (!fetched.TryGetValue(meter, out var list))
                    {
                        fetched[meter] = list = [];
                    }
                    list.AddRange(meterReadings.Readings);
                }
            }
            StoreDays(cachedDays, pieceMeters, missingSet, answer, today, pieceFrom, pieceTo);
        }
        return new FetchOutcome<TReading>(fetched, null, false);
    }

    // A meter the cached meter list doesn't know is assumed to be installed. If the list holds the
    // meter more than once (several installations), it counts as installed when any of them says so.
    private static bool IsInstalled(IEnumerable<Meter> records, DateOnly day) =>
        !records.Any()
        || records.Any(m => (m.InstallDate is null || day >= m.InstallDate) && (m.RemovalDate is null || day <= m.RemovalDate));

    // Caches the missing past days that lie wholly inside the piece: complete days, and incomplete ones
    // (even without any readings) once they are old enough to be final. Only called for a non-empty answer,
    // so a meter or day missing from it really has no data at SEV.
    private static void StoreDays<TReading>(
        Dictionary<(int MeterId, DateOnly Day), IReadOnlyList<TReading>> cachedDays,
        IReadOnlyList<int> meters,
        HashSet<(int Meter, DateOnly Day)> missingSet,
        List<MeterReadings<TReading>> answer,
        DateOnly today,
        DateTime pieceFrom,
        DateTime pieceTo)
        where TReading : IHourlyReading
    {
        var byMeterAndDay = answer
            .SelectMany(m => m.Readings, (m, r) => (m.MeterId, Reading: r))
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
                if (!missingSet.Contains((meter, day)))
                {
                    continue;
                }
                var readings = byMeterAndDay[(meter.ToString(), day)].OrderBy(r => r.TimeStamp).ToList();
                if (readings.Count >= SevCache.MinReadingsForCompleteDay || day < finalBefore)
                {
                    cachedDays[(meter, day)] = readings;
                }
            }
        }
    }

    // Readings for one meter within [from, to): cached days first, fetched data for the rest.
    // Null when there is no data at all for the meter (neither cached nor in SEV's answer).
    private static List<TReading>? Collect<TReading>(
        Dictionary<(int MeterId, DateOnly Day), IReadOnlyList<TReading>> cachedDays,
        int meter,
        IReadOnlyList<DateOnly> days,
        Dictionary<int, List<TReading>> fetched,
        DateTime from,
        DateTime to)
        where TReading : IHourlyReading
    {
        var hasFetched = fetched.TryGetValue(meter, out var fetchedReadings);
        var anyCached = false;
        var readings = new List<TReading>();

        foreach (var day in days)
        {
            if (cachedDays.TryGetValue((meter, day), out var cached))
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

    // Groups missing days (sorted ascending) into pieces spanning at most MaxDaysPerRequest calendar days,
    // newest first. Gaps larger than one piece are skipped so already-cached stretches aren't re-fetched,
    // while small cached gaps inside a 31-day window stay in one call. Boundaries fall on midnight (or
    // from/fetchEnd for today), so every whole past day lies inside one piece and can be cached.
    private static List<Piece> PiecesNewestFirst(
        IReadOnlyList<DateOnly> missingDaysAscending, DateTime from, DateTime fetchEnd, DateOnly today)
    {
        var todayStart = today.ToDateTime(TimeOnly.MinValue);
        var pieces = new List<Piece>();
        for (var lastIndex = missingDaysAscending.Count - 1; lastIndex >= 0;)
        {
            var lastDay = missingDaysAscending[lastIndex];
            var limit = lastDay.AddDays(-(MaxDaysPerRequest - 1));
            var firstIndex = lastIndex;
            while (firstIndex > 0 && missingDaysAscending[firstIndex - 1] >= limit)
            {
                firstIndex--;
            }

            var firstDay = missingDaysAscending[firstIndex];
            var pieceFrom = firstDay == today ? Max(from, todayStart) : firstDay.ToDateTime(TimeOnly.MinValue);
            var pieceTo = lastDay == today ? fetchEnd : lastDay.AddDays(1).ToDateTime(TimeOnly.MinValue);
            pieces.Add(new Piece(firstDay, lastDay, pieceFrom, pieceTo));
            lastIndex = firstIndex - 1;
        }
        return pieces;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
