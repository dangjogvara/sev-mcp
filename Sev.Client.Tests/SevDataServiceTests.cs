using System.Globalization;
using System.Text.Json;
using Sev.Client.Models;

namespace Sev.Client.Tests;

public class SevDataServiceTests
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

        AssertRequest(_handler.Requests[1], [1111], Start(Today), Start(Today.AddDays(1)));
        Assert.Equal(36, result.Single().Readings.Count);
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
    public async Task Usage_EmptyAnswerIsReturnedAndNotCached()
    {
        _handler.Respond(UsageJson((1111, Day(-3), 24)));
        _handler.Respond("[]");
        _handler.Respond(UsageJson((1111, Day(-2), 24)));

        await UsageAsync([1111], Start(Day(-3)), Start(Day(-2)));
        // Day -3 is cached, but day -2 is missing and SEV answers empty: return nothing rather than half the range.
        Assert.Empty(await UsageAsync([1111], Start(Day(-3)), Start(Day(-1))));
        var retry = await UsageAsync([1111], Start(Day(-3)), Start(Day(-1)));

        Assert.Equal(3, _handler.Requests.Count);
        Assert.Equal(48, retry.Single().Readings.Count);
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
        // 40 days ending at 15:00 today: the newest piece is 30 whole days plus today's part.
        _handler.Respond(UsageJson(DaysOf(1111, Day(-30), 30)));
        _handler.Respond(UsageJson(DaysOf(1111, Day(-40), 10)));

        await _data.GetHourlyUsageAsync([1111], Start(Day(-40)), Start(Today).AddHours(15));

        AssertRequest(_handler.Requests[0], [1111], Start(Day(-30)), Start(Today).AddHours(15));
        AssertRequest(_handler.Requests[1], [1111], Start(Day(-40)), Start(Day(-30)));
    }

    // For tests that only look at the readings, not at what wasn't fetched.
    private async Task<IReadOnlyList<MeterUsage>> UsageAsync(IReadOnlyList<int> meterIds, DateTime from, DateTime to) =>
        (await _data.GetHourlyUsageAsync(meterIds, from, to)).Meters;

    // (meter, day, 24) for `count` days from `first`.
    private static (int Meter, DateOnly Day, int Hours)[] DaysOf(int meter, DateOnly first, int count) =>
        Enumerable.Range(0, count).Select(d => (meter, first.AddDays(d), 24)).ToArray();

    private static DateOnly Day(int offsetFromToday) => Today.AddDays(offsetFromToday);

    private static DateTime Start(DateOnly day) => day.ToDateTime(TimeOnly.MinValue);

    private static void AssertRequest(RecordedRequest request, int[] meters, DateTime from, DateTime to)
    {
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(meters, body.RootElement.GetProperty("meters").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(from.ToString("s", CultureInfo.InvariantCulture), body.RootElement.GetProperty("from_date").GetString());
        Assert.Equal(to.ToString("s", CultureInfo.InvariantCulture), body.RootElement.GetProperty("to_date").GetString());
    }

    // A hourly_kwh_usage response with `hours` readings from 00:00 for each (meter, day).
    private static string UsageJson(params (int Meter, DateOnly Day, int Hours)[] parts)
    {
        var meters = parts
            .GroupBy(p => p.Meter)
            .Select(g => new
            {
                meter_id = g.Key.ToString(),
                readings = g.SelectMany(p => Enumerable.Range(0, p.Hours).Select(h => new
                {
                    time_stamp = Start(p.Day).AddHours(h).ToString("s", CultureInfo.InvariantCulture),
                    reading = 1.0m,
                    cumulative_value = (decimal?)null,
                    unit = "kWh",
                })),
            });
        return JsonSerializer.Serialize(meters);
    }
}
