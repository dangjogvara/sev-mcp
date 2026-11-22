using System.Globalization;
using System.Text.Json;
using Sev.Client.Models;

namespace Sev.Client.Tests;

public partial class SevDataServiceTests
{
    // The fake clock starts at 2026-11-21 12:00 UTC, which is 12:00 Faroese time (winter, UTC+0).
    private static readonly DateOnly Today = new(2026, 11, 21);

    private readonly FakeTimeProvider _time = new();
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly SevCache _cache;
    private readonly SevDataService _data;

    public SevDataServiceTests()
    {
        _cache = new SevCache(_time);
        var client = new SevClient(new HttpClient(_handler) { BaseAddress = SevClient.DefaultBaseAddress });
        _data = new SevDataService(client, _cache);
    }

    // --- Meter list ---

    [Fact]
    public async Task Meters_SecondCallComesFromCache()
    {
        _handler.RespondWithFixture("get_available_meters.json");

        var first = await _data.GetAvailableMetersAsync();
        var second = await _data.GetAvailableMetersAsync();

        Assert.Single(_handler.Requests);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task Meters_RefetchedAfterLifetime()
    {
        _handler.RespondWithFixture("get_available_meters.json");
        _handler.RespondWithFixture("get_available_meters.json");

        await _data.GetAvailableMetersAsync();
        _time.Advance(SevCache.MeterListLifetime);
        await _data.GetAvailableMetersAsync();

        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Meters_EmptyAnswerIsNotCached()
    {
        _handler.Respond("[]");
        _handler.RespondWithFixture("get_available_meters.json");

        Assert.Empty(await _data.GetAvailableMetersAsync());
        Assert.Single(await _data.GetAvailableMetersAsync());
        Assert.Equal(2, _handler.Requests.Count);
    }

    // --- Usage ---

    [Fact]
    public async Task Usage_PastDaysComeFromCacheTheSecondTime()
    {
        _handler.Respond(UsageJson((1111, Day(-3), 24), (1111, Day(-2), 24)));

        var first = await UsageAsync([1111], Start(Day(-3)), Start(Day(-1)));
        var second = await UsageAsync([1111], Start(Day(-3)), Start(Day(-1)));

        Assert.Single(_handler.Requests);
        Assert.Equal(48, Assert.Single(first).Readings.Count);
        Assert.Equal(first.Single().Readings, second.Single().Readings);
    }

    [Fact]
    public async Task Usage_PartOfAPastDayIsFetchedAsWholeDay_ThenServedFromCache()
    {
        _handler.Respond(UsageJson((1111, Day(-3), 24)));

        var morning = await UsageAsync([1111], Start(Day(-3)).AddHours(6), Start(Day(-3)).AddHours(9));
        var wholeDay = await UsageAsync([1111], Start(Day(-3)), Start(Day(-2)));

        Assert.Single(_handler.Requests);
        AssertRequest(_handler.Requests[0], [1111], Start(Day(-3)), Start(Day(-2)));
        Assert.Equal([6, 7, 8], morning.Single().Readings.Select(r => r.TimeStamp.Hour));
        Assert.Equal(24, wholeDay.Single().Readings.Count);
    }

    [Fact]
    public async Task Usage_OnlyMissingDaysAreFetched()
    {
        _handler.Respond(UsageJson((1111, Day(-5), 24), (1111, Day(-4), 24)));
        _handler.Respond(UsageJson((1111, Day(-3), 24)));

        await UsageAsync([1111], Start(Day(-5)), Start(Day(-3)));
        var result = await UsageAsync([1111], Start(Day(-5)), Start(Day(-2)));

        Assert.Equal(2, _handler.Requests.Count);
        AssertRequest(_handler.Requests[1], [1111], Start(Day(-3)), Start(Day(-2)));
        Assert.Equal(72, result.Single().Readings.Count);
    }

    [Fact]
    public async Task Usage_OnlyMetersWithMissingDaysAreFetched()
    {
        _handler.Respond(UsageJson((1111, Day(-3), 24)));
        _handler.Respond(UsageJson((2222, Day(-3), 24)));

        await UsageAsync([1111], Start(Day(-3)), Start(Day(-2)));
        var result = await UsageAsync([1111, 2222], Start(Day(-3)), Start(Day(-2)));

        AssertRequest(_handler.Requests[1], [2222], Start(Day(-3)), Start(Day(-2)));
        Assert.Equal(["1111", "2222"], result.Select(m => m.MeterId));
        Assert.All(result, m => Assert.Equal(24, m.Readings.Count));
    }

    [Fact]
    public async Task Usage_TodayIsNeverCached()
    {
        _handler.Respond(UsageJson((1111, Today, 12)));
        _handler.Respond(UsageJson((1111, Today, 12)));

        await UsageAsync([1111], Start(Today), Start(Today.AddDays(1)));
        await UsageAsync([1111], Start(Today), Start(Today.AddDays(1)));

        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Usage_RangeIntoToday_FetchesOnlyTodayWhenPastDaysAreCached()
    {
        _handler.Respond(UsageJson((1111, Day(-1), 24)));
        _handler.Respond(UsageJson((1111, Today, 12)));

        await UsageAsync([1111], Start(Day(-1)), Start(Today));
        var result = await UsageAsync([1111], Start(Day(-1)), Start(Today.AddDays(1)));

        // The fake clock is at 12:00, so today is fetched up to then and not to midnight.
        AssertRequest(_handler.Requests[1], [1111], Start(Today), Start(Today).AddHours(12));
        Assert.Equal(36, result.Single().Readings.Count);
    }

    // --- Only what can exist is fetched ---

    [Fact]
    public async Task Usage_FutureRange_DoesNotCallSev()
    {
        var result = await _data.GetHourlyUsageAsync([1111], Start(Day(1)), Start(Day(2)));

        Assert.Empty(_handler.Requests);
        Assert.Empty(result.Meters);
        Assert.Null(result.NotFetched);
        Assert.False(result.EmptyAnswer);
    }

    [Fact]
    public async Task Usage_RangeStartingAtCurrentHour_DoesNotCallSev()
    {
        // 12:00 to 15:00 today at 12:00: no hour of it has ended yet.
        var result = await _data.GetHourlyUsageAsync([1111], Start(Today).AddHours(12), Start(Today).AddHours(15));

        Assert.Empty(_handler.Requests);
        Assert.Empty(result.Meters);
        Assert.False(result.EmptyAnswer);
    }

    [Fact]
    public async Task Usage_DaysBeforeInstallDate_AreNotFetched()
    {
        await LoadMetersAsync((1111, Day(-2), null));
        _handler.Respond(UsageJson((1111, Day(-2), 24), (1111, Day(-1), 24)));

        var first = await UsageAsync([1111], Start(Day(-5)), Start(Today));
        var second = await UsageAsync([1111], Start(Day(-5)), Start(Today));

        Assert.Equal(2, _handler.Requests.Count); // meter list + one usage call
        AssertRequest(_handler.Requests[1], [1111], Start(Day(-2)), Start(Today));
        Assert.Equal(48, first.Single().Readings.Count);
        Assert.Equal(48, second.Single().Readings.Count);
    }

    [Fact]
    public async Task Usage_DaysAfterRemovalDate_AreNotFetched()
    {
        await LoadMetersAsync((1111, null, Day(-4)));
        _handler.Respond(UsageJson((1111, Day(-5), 24), (1111, Day(-4), 24)));

        await UsageAsync([1111], Start(Day(-5)), Start(Today));
        var second = await UsageAsync([1111], Start(Day(-5)), Start(Today));

        Assert.Equal(2, _handler.Requests.Count);
        AssertRequest(_handler.Requests[1], [1111], Start(Day(-5)), Start(Day(-3)));
        Assert.Equal(48, second.Single().Readings.Count);
    }

    [Fact]
    public async Task Usage_AllDaysOutsideInstallation_DoesNotCallSev()
    {
        await LoadMetersAsync((1111, Day(10), null));

        var result = await _data.GetHourlyUsageAsync([1111], Start(Day(-5)), Start(Today));

        Assert.Single(_handler.Requests); // meter list only
        Assert.Empty(result.Meters);
        Assert.False(result.EmptyAnswer);
    }

    [Fact]
    public async Task Usage_OldIncompleteDay_IsCachedAsFinal()
    {
        var old = Day(-SevCache.DaysUntilFinal);
        _handler.Respond(UsageJson((1111, old, 20)));

        await UsageAsync([1111], Start(old), Start(old.AddDays(1)));
        var second = await UsageAsync([1111], Start(old), Start(old.AddDays(1)));

        Assert.Single(_handler.Requests);
        Assert.Equal(20, second.Single().Readings.Count);
    }

    [Fact]
    public async Task Usage_OldDaysWithoutData_AreCachedAsEmpty()
    {
        // SEV answers for meter 1111 but has nothing for 2222, or for 1111 on the first day.
        var old = Day(-10);
        _handler.Respond(UsageJson((1111, old.AddDays(1), 24)));

        await UsageAsync([1111, 2222], Start(old), Start(old.AddDays(2)));
        var second = await UsageAsync([1111, 2222], Start(old), Start(old.AddDays(2)));

        Assert.Single(_handler.Requests);
        Assert.Equal(["1111", "2222"], second.Select(m => m.MeterId));
        Assert.Equal(24, second[0].Readings.Count);
        Assert.Empty(second[1].Readings);
    }

    [Fact]
    public async Task Usage_RecentDayWithoutData_IsFetchedAgain()
    {
        _handler.Respond(UsageJson((1111, Day(-1), 24)));
        _handler.Respond(UsageJson((1111, Day(-2), 24)));

        await UsageAsync([1111], Start(Day(-2)), Start(Today));
        var second = await UsageAsync([1111], Start(Day(-2)), Start(Today));

        Assert.Equal(2, _handler.Requests.Count);
        AssertRequest(_handler.Requests[1], [1111], Start(Day(-2)), Start(Day(-1)));
        Assert.Equal(48, second.Single().Readings.Count);
    }

    [Fact]
    public async Task Usage_IncompleteDayIsNotCached()
    {
        _handler.Respond(UsageJson((1111, Day(-1), 20)));
        _handler.Respond(UsageJson((1111, Day(-1), 24)));

        var first = await UsageAsync([1111], Start(Day(-1)), Start(Today));
        var second = await UsageAsync([1111], Start(Day(-1)), Start(Today));

        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal(20, first.Single().Readings.Count);
        Assert.Equal(24, second.Single().Readings.Count);
    }

    [Fact]
    public async Task Usage_EmptyAnswer_ReturnsCachedDaysAndReportsTheRestAsNotFetched()
    {
        _handler.Respond(UsageJson((1111, Day(-3), 24)));
        _handler.Respond("[]");
        _handler.Respond(UsageJson((1111, Day(-2), 24)));

        await UsageAsync([1111], Start(Day(-3)), Start(Day(-2)));
        // Day -3 is cached, but day -2 is missing and SEV answers empty (likely throttling).
        var throttled = await _data.GetHourlyUsageAsync([1111], Start(Day(-3)), Start(Day(-1)));
        var retry = await UsageAsync([1111], Start(Day(-3)), Start(Day(-1)));

        Assert.True(throttled.EmptyAnswer);
        Assert.Equal((Start(Day(-2)), Start(Day(-1))), throttled.NotFetched);
        Assert.Equal(24, throttled.Meters.Single().Readings.Count);
        Assert.Equal(3, _handler.Requests.Count);
        Assert.Equal(48, retry.Single().Readings.Count);
    }

    [Fact]
    public async Task Usage_EmptyAnswerWithNothingCached_IsReportedAndNotCached()
    {
        _handler.Respond("[]");
        _handler.Respond(UsageJson((1111, Day(-2), 24)));

        var throttled = await _data.GetHourlyUsageAsync([1111], Start(Day(-2)), Start(Day(-1)));
        var retry = await UsageAsync([1111], Start(Day(-2)), Start(Day(-1)));

        Assert.True(throttled.EmptyAnswer);
        Assert.Empty(throttled.Meters);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal(24, retry.Single().Readings.Count);
    }

    [Fact]
    public async Task Usage_InvalidRangeIsRejectedWithoutCallingApi()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => UsageAsync([1111], Start(Day(-1)), Start(Day(-2))));

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Usage_ConcurrentRequestsForSameDayFetchOnce()
    {
        var gate = new TaskCompletionSource();
        _handler.Gate = gate.Task;
        _handler.Respond(UsageJson((1111, Day(-1), 24)));

        var calls = Enumerable.Range(0, 5)
            .Select(_ => UsageAsync([1111], Start(Day(-1)), Start(Today)))
            .ToList();
        gate.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.Single(_handler.Requests);
        Assert.All(results, r => Assert.Equal(24, r.Single().Readings.Count));
    }

    // --- Long ranges: SEV returns at most 31 days per call ---

    [Fact]
    public async Task Usage_LongRangeIsFetchedIn31DayPiecesNewestFirst()
    {
        // 60 days before today: two pieces, the newest 31 days first.
        _handler.Respond(UsageJson(DaysOf(1111, Day(-31), 31)));
        _handler.Respond(UsageJson(DaysOf(1111, Day(-60), 29)));

        var result = await _data.GetHourlyUsageAsync([1111], Start(Day(-60)), Start(Today));

        Assert.Equal(2, _handler.Requests.Count);
        AssertRequest(_handler.Requests[0], [1111], Start(Day(-31)), Start(Today));
        AssertRequest(_handler.Requests[1], [1111], Start(Day(-60)), Start(Day(-31)));
        Assert.Null(result.NotFetched);
        Assert.Equal(60 * 24, result.Meters.Single().Readings.Count);
    }

    [Fact]
    public async Task Usage_AtMostSixPiecesPerCall_RestIsReportedAndFetchedNextTime()
    {
        // 8 pieces of 31 days = 248 days.
        for (var piece = 1; piece <= 8; piece++)
        {
            _handler.Respond(UsageJson(DaysOf(1111, Day(-31 * piece), 31)));
        }

        var first = await _data.GetHourlyUsageAsync([1111], Start(Day(-248)), Start(Today));

        Assert.Equal(SevDataService.MaxRequestsPerCall, _handler.Requests.Count);
        Assert.Equal((Start(Day(-248)), Start(Day(-186))), first.NotFetched);
        Assert.Equal(186 * 24, first.Meters.Single().Readings.Count);

        // The fetched days are cached, so the second call only fetches the two oldest pieces.
        var second = await _data.GetHourlyUsageAsync([1111], Start(Day(-248)), Start(Today));

        Assert.Equal(8, _handler.Requests.Count);
        AssertRequest(_handler.Requests[6], [1111], Start(Day(-217)), Start(Day(-186)));
        AssertRequest(_handler.Requests[7], [1111], Start(Day(-248)), Start(Day(-217)));
        Assert.Null(second.NotFetched);
        Assert.Equal(248 * 24, second.Meters.Single().Readings.Count);
    }

    [Fact]
    public async Task Usage_EmptyAnswerPartway_KeepsWhatWasFetched()
    {
        _handler.Respond(UsageJson(DaysOf(1111, Day(-31), 31)));
        _handler.Respond("[]");

        var result = await _data.GetHourlyUsageAsync([1111], Start(Day(-60)), Start(Today));

        Assert.Equal((Start(Day(-60)), Start(Day(-31))), result.NotFetched);
        Assert.Equal(31 * 24, result.Meters.Single().Readings.Count);
    }

    [Fact]
    public async Task Usage_RateLimitPartway_KeepsWhatWasFetched()
    {
        var limited = new SevDataService(
            new SevClient(new HttpClient(new SevRateLimitHandler(new SevRateLimiter(_time, maxCalls: 1)) { InnerHandler = _handler })
            {
                BaseAddress = SevClient.DefaultBaseAddress
            }),
            _cache);
        _handler.Respond(UsageJson(DaysOf(1111, Day(-31), 31)));

        var result = await limited.GetHourlyUsageAsync([1111], Start(Day(-60)), Start(Today));

        Assert.Single(_handler.Requests);
        Assert.Equal((Start(Day(-60)), Start(Day(-31))), result.NotFetched);
        Assert.Equal(31 * 24, result.Meters.Single().Readings.Count);
    }

    [Fact]
    public async Task Usage_RangeIntoToday_PiecesEndOnMidnight()
    {
        // 40 days ending at 10:00 today: the newest piece is 30 whole days plus today's part.
        _handler.Respond(UsageJson(DaysOf(1111, Day(-30), 30)));
        _handler.Respond(UsageJson(DaysOf(1111, Day(-40), 10)));

        await _data.GetHourlyUsageAsync([1111], Start(Day(-40)), Start(Today).AddHours(10));

        AssertRequest(_handler.Requests[0], [1111], Start(Day(-30)), Start(Today).AddHours(10));
        AssertRequest(_handler.Requests[1], [1111], Start(Day(-40)), Start(Day(-30)));
    }

    [Fact]
    public async Task Usage_LongRangeIncludingToday_MakesProgressAcrossCalls()
    {
        // Call 1: 6 pieces newest-first covering Day(-185)..Today 12:00.
        _handler.Respond(UsageJson(DaysOf(1111, Day(-30), 30).Concat([(1111, Today, 12)]).ToArray()));
        for (var piece = 1; piece < SevDataService.MaxRequestsPerCall; piece++)
        {
            _handler.Respond(UsageJson(DaysOf(1111, Day(-30 - 31 * piece), 31)));
        }

        // Call 2: Today (never cached) plus the remaining 3 older pieces, skipping the 185 cached days.
        _handler.Respond(UsageJson((1111, Today, 12)));
        _handler.Respond(UsageJson(DaysOf(1111, Day(-216), 31)));
        _handler.Respond(UsageJson(DaysOf(1111, Day(-247), 31)));
        _handler.Respond(UsageJson(DaysOf(1111, Day(-248), 1)));

        var first = await _data.GetHourlyUsageAsync([1111], Start(Day(-248)), Start(Today.AddDays(1)));

        Assert.Equal(SevDataService.MaxRequestsPerCall, _handler.Requests.Count);
        Assert.Equal((Start(Day(-248)), Start(Day(-185))), first.NotFetched);
        Assert.Equal(185 * 24 + 12, first.Meters.Single().Readings.Count);

        var second = await _data.GetHourlyUsageAsync([1111], Start(Day(-248)), Start(Today.AddDays(1)));

        Assert.Equal(10, _handler.Requests.Count);
        AssertRequest(_handler.Requests[6], [1111], Start(Today), Start(Today).AddHours(12));
        AssertRequest(_handler.Requests[7], [1111], Start(Day(-216)), Start(Day(-185)));
        AssertRequest(_handler.Requests[8], [1111], Start(Day(-247)), Start(Day(-216)));
        AssertRequest(_handler.Requests[9], [1111], Start(Day(-248)), Start(Day(-247)));
        Assert.Null(second.NotFetched);
        Assert.Equal(248 * 24 + 12, second.Meters.Single().Readings.Count);
    }

    [Fact]
    public async Task Usage_CachedMiddleStretch_IsSkippedWhenFetchingOlderAndNewerGaps()
    {
        // Pre-cache Day(-45)..Day(-5) (41 days, fetched in 2 pieces).
        _handler.Respond(UsageJson(DaysOf(1111, Day(-35), 31)));
        _handler.Respond(UsageJson(DaysOf(1111, Day(-45), 10)));
        await UsageAsync([1111], Start(Day(-45)), Start(Day(-4)));

        // Query Day(-50)..Today: only the newer gap and older gap are fetched.
        _handler.Respond(UsageJson(DaysOf(1111, Day(-4), 4).Concat([(1111, Today, 12)]).ToArray()));
        _handler.Respond(UsageJson(DaysOf(1111, Day(-50), 5)));

        var result = await _data.GetHourlyUsageAsync([1111], Start(Day(-50)), Start(Today.AddDays(1)));

        Assert.Equal(4, _handler.Requests.Count);
        AssertRequest(_handler.Requests[2], [1111], Start(Day(-4)), Start(Today).AddHours(12));
        AssertRequest(_handler.Requests[3], [1111], Start(Day(-50)), Start(Day(-45)));
        Assert.Null(result.NotFetched);
        Assert.Equal(50 * 24 + 12, result.Meters.Single().Readings.Count);
    }

    [Fact]
    public async Task Usage_ShortCachedGapWithin31Days_IsFetchedInOneCall()
    {
        _handler.Respond(UsageJson(DaysOf(1111, Day(-5), 1)));
        await UsageAsync([1111], Start(Day(-5)), Start(Day(-4)));

        _handler.Respond(UsageJson(DaysOf(1111, Day(-10), 10)));
        var result = await _data.GetHourlyUsageAsync([1111], Start(Day(-10)), Start(Today));

        Assert.Equal(2, _handler.Requests.Count);
        AssertRequest(_handler.Requests[1], [1111], Start(Day(-10)), Start(Today));
        Assert.Equal(10 * 24, result.Meters.Single().Readings.Count);
    }

    [Fact]
    public async Task Usage_MixedInstallDates_DoesNotCacheEmptyDaysBeforeNewerMeterInstall()
    {
        await LoadMetersAsync((1111, Day(-20), null), (2222, Day(-10), null));
        _handler.Respond(UsageJson(DaysOf(1111, Day(-15), 7).Concat(DaysOf(2222, Day(-10), 2)).ToArray()));

        await _data.GetHourlyUsageAsync([1111, 2222], Start(Day(-15)), Start(Day(-8)));

        // If the meter list later reports an earlier install date for 2222, its pre-install days must not
        // have been cached as empty when 1111 fetched that range.
        _time.Advance(SevCache.MeterListLifetime);
        await LoadMetersAsync((1111, Day(-20), null), (2222, Day(-20), null));
        _handler.Respond(UsageJson(DaysOf(2222, Day(-15), 5)));

        var second = await _data.GetHourlyUsageAsync([1111, 2222], Start(Day(-15)), Start(Day(-8)));

        Assert.Equal(4, _handler.Requests.Count);
        AssertRequest(_handler.Requests[3], [2222], Start(Day(-15)), Start(Day(-10)));
        Assert.Equal(7 * 24, second.Meters[0].Readings.Count);
        Assert.Equal(7 * 24, second.Meters[1].Readings.Count);
    }

    // --- Cost: same fetching and caching as usage, in its own cache ---

    [Fact]
    public async Task Cost_PastDaysComeFromCacheTheSecondTime()
    {
        _handler.Respond(CostJson((1111, Day(-2), 24)));

        var first = await _data.GetEstimatedCostAsync([1111], Start(Day(-2)), Start(Day(-1)));
        var second = await _data.GetEstimatedCostAsync([1111], Start(Day(-2)), Start(Day(-1)));

        var request = Assert.Single(_handler.Requests);
        Assert.EndsWith("estimated_cost", request.Uri.ToString());
        Assert.Equal(24, first.Meters.Single().Readings.Count);
        Assert.Equal(first.Meters.Single().Readings, second.Meters.Single().Readings);
        Assert.Equal(1.825m, second.Meters.Single().Readings[0].PriceUnit);
    }

    [Fact]
    public async Task Cost_AndUsage_AreCachedSeparately()
    {
        _handler.Respond(UsageJson((1111, Day(-2), 24)));
        _handler.Respond(CostJson((1111, Day(-2), 24)));

        await UsageAsync([1111], Start(Day(-2)), Start(Day(-1)));
        var cost = await _data.GetEstimatedCostAsync([1111], Start(Day(-2)), Start(Day(-1)));

        Assert.Equal(2, _handler.Requests.Count);
        Assert.EndsWith("hourly_kwh_usage", _handler.Requests[0].Uri.ToString());
        Assert.EndsWith("estimated_cost", _handler.Requests[1].Uri.ToString());
        Assert.All(cost.Meters.Single().Readings, r => Assert.Equal("DKR", r.Unit));
    }

    [Fact]
    public async Task Cost_LongRange_IsFetchedInPiecesAndReportsWhatDidNotFit()
    {
        // 8 pieces of 31 days: 6 are fetched, the rest is reported as not fetched.
        for (var piece = 1; piece <= SevDataService.MaxRequestsPerCall; piece++)
        {
            _handler.Respond(CostJson(DaysOf(1111, Day(-31 * piece), 31)));
        }

        var result = await _data.GetEstimatedCostAsync([1111], Start(Day(-248)), Start(Today));

        Assert.Equal(SevDataService.MaxRequestsPerCall, _handler.Requests.Count);
        Assert.Equal((Start(Day(-248)), Start(Day(-186))), result.NotFetched);
        Assert.Equal(186 * 24, result.Meters.Single().Readings.Count);
    }

    [Fact]
    public async Task Cost_EmptyAnswer_IsReported()
    {
        _handler.Respond("[]");

        var result = await _data.GetEstimatedCostAsync([1111], Start(Day(-2)), Start(Day(-1)));

        Assert.True(result.EmptyAnswer);
        Assert.Empty(result.Meters);
    }

    // --- CO2: same fetching and caching as usage, in its own cache ---

    [Fact]
    public async Task Co2_PastDaysComeFromCacheTheSecondTime()
    {
        _handler.Respond(Co2Json((1111, Day(-2), 24)));

        var first = await _data.GetEstimatedCo2Async([1111], Start(Day(-2)), Start(Day(-1)));
        var second = await _data.GetEstimatedCo2Async([1111], Start(Day(-2)), Start(Day(-1)));

        var request = Assert.Single(_handler.Requests);
        Assert.EndsWith("estimated_CO2", request.Uri.ToString());
        Assert.Equal(24, first.Meters.Single().Readings.Count);
        Assert.Equal(first.Meters.Single().Readings, second.Meters.Single().Readings);
    }

    [Fact]
    public async Task Co2_IsCachedSeparatelyFromUsageAndCost()
    {
        _handler.Respond(UsageJson((1111, Day(-2), 24)));
        _handler.Respond(CostJson((1111, Day(-2), 24)));
        _handler.Respond(Co2Json((1111, Day(-2), 24)));

        await UsageAsync([1111], Start(Day(-2)), Start(Day(-1)));
        await _data.GetEstimatedCostAsync([1111], Start(Day(-2)), Start(Day(-1)));
        var co2 = await _data.GetEstimatedCo2Async([1111], Start(Day(-2)), Start(Day(-1)));

        Assert.Equal(3, _handler.Requests.Count);
        Assert.EndsWith("estimated_CO2", _handler.Requests[2].Uri.ToString());
        Assert.All(co2.Meters.Single().Readings, r => Assert.Equal("Kg CO2", r.Unit));
    }

    // For tests that only look at the readings, not at what wasn't fetched.
    private async Task<IReadOnlyList<MeterUsage>> UsageAsync(IReadOnlyList<int> meterIds, DateTime from, DateTime to) =>
        (await _data.GetHourlyUsageAsync(meterIds, from, to)).Meters;

    // (meter, day, 24) for `count` days from `first`.
    private static (int Meter, DateOnly Day, int Hours)[] DaysOf(int meter, DateOnly first, int count) =>
        Enumerable.Range(0, count).Select(d => (meter, first.AddDays(d), 24)).ToArray();

    // Puts a meter list with these install and removal dates in the cache (costs one recorded request).
    private async Task LoadMetersAsync(params (int Meter, DateOnly? Installed, DateOnly? Removed)[] meters)
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new
            {
                customer_name = "Test Customer",
                installations = new[]
                {
                    new
                    {
                        move_in_date = "2019-06-01T00:00:00",
                        inst_id = 10001,
                        address = "Testgøta 1",
                        postal_code = "100",
                        postal_district = "Tórshavn",
                        tarif_number = 310,
                        billing_type = "OS",
                        meters = meters.Select(m => new
                        {
                            meter_id = m.Meter,
                            serial_number = "11111111",
                            meter_name = "E350",
                            meter_type = "E-01",
                            install_date = m.Installed?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            removal_date = m.Removed?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        }),
                    },
                },
            },
        });
        _handler.Respond(json);
        await _data.GetAvailableMetersAsync();
    }

    private static DateOnly Day(int offsetFromToday) => Today.AddDays(offsetFromToday);

    private static DateTime Start(DateOnly day) => day.ToDateTime(TimeOnly.MinValue);

    private static void AssertRequest(RecordedRequest request, int[] meters, DateTime from, DateTime to)
    {
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(meters, body.RootElement.GetProperty("meters").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(from.ToString("s", CultureInfo.InvariantCulture), body.RootElement.GetProperty("from_date").GetString());
        Assert.Equal(to.ToString("s", CultureInfo.InvariantCulture), body.RootElement.GetProperty("to_date").GetString());
    }

    // A hourly_kwh_usage response: 1 kWh per hour.
    private static string UsageJson(params (int Meter, DateOnly Day, int Hours)[] parts) =>
        HourlyJson(parts, time => new { time_stamp = time, reading = 1.0m, cumulative_value = (decimal?)null, unit = "kWh" });

    // An estimated_cost response: 1.825 DKK per hour.
    private static string CostJson(params (int Meter, DateOnly Day, int Hours)[] parts) =>
        HourlyJson(parts, time => new { time_stamp = time, reading = 1.825m, unit = "DKR", price_unit = 1.825m, tarif = 310 });

    // An estimated_CO2 response: 0.5 kg CO2 per hour.
    private static string Co2Json(params (int Meter, DateOnly Day, int Hours)[] parts) =>
        HourlyJson(parts, time => new { time_stamp = time, reading = 0.5m, cumulative_value = (decimal?)null, unit = "Kg CO2" });

    // A response from any hourly endpoint, with `hours` readings from 00:00 for each (meter, day).
    // `reading` builds one reading from its time stamp, in that endpoint's shape.
    private static string HourlyJson((int Meter, DateOnly Day, int Hours)[] parts, Func<string, object> reading)
    {
        var meters = parts
            .GroupBy(p => p.Meter)
            .Select(g => new
            {
                meter_id = g.Key.ToString(),
                readings = g.SelectMany(p => Enumerable.Range(0, p.Hours)
                    .Select(h => reading(Start(p.Day).AddHours(h).ToString("s", CultureInfo.InvariantCulture)))),
            });
        return JsonSerializer.Serialize(meters);
    }
}
