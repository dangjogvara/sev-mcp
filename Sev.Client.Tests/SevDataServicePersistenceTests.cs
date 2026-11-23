using System.Net;
using System.Text.Json;

namespace Sev.Client.Tests;

// SevCacheFiles: what SevDataService caches is saved to a directory and loaded by the next SevCache,
// as after a server restart. Shares the fake clock, fake network and helpers with the other tests.
public partial class SevDataServiceTests : IDisposable
{
    private readonly string _cacheDirectory =
        Path.Combine(Path.GetTempPath(), "sev-mcp-tests", Guid.NewGuid().ToString("N"));

    private readonly List<string> _log = [];

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Persisted_UsageDaysAreServedAfterRestart()
    {
        _handler.Respond(UsageJson((1111, Day(-3), 24), (1111, Day(-2), 24)));
        var before = await StartServer().GetHourlyUsageAsync([1111], Start(Day(-3)), Start(Day(-1)));

        var after = await StartServer().GetHourlyUsageAsync([1111], Start(Day(-3)), Start(Day(-1)));

        Assert.Single(_handler.Requests);
        Assert.Equal(48, after.Meters.Single().Readings.Count);
        Assert.Equal(before.Meters.Single().Readings, after.Meters.Single().Readings);
        Assert.Contains(_log, line => line.Contains("Loaded 2 cached days from UsageReading.json"));
    }

    [Fact]
    public async Task Persisted_CostAndCo2DaysAreServedAfterRestart()
    {
        var server = StartServer();
        _handler.Respond(CostJson((1111, Day(-2), 24)));
        _handler.Respond(Co2Json((1111, Day(-2), 24)));
        var cost = await server.GetEstimatedCostAsync([1111], Start(Day(-2)), Start(Day(-1)));
        var co2 = await server.GetEstimatedCo2Async([1111], Start(Day(-2)), Start(Day(-1)));

        var restarted = StartServer();
        var costAfter = await restarted.GetEstimatedCostAsync([1111], Start(Day(-2)), Start(Day(-1)));
        var co2After = await restarted.GetEstimatedCo2Async([1111], Start(Day(-2)), Start(Day(-1)));

        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal(cost.Meters.Single().Readings, costAfter.Meters.Single().Readings);
        Assert.Equal(co2.Meters.Single().Readings, co2After.Meters.Single().Readings);
    }

    [Fact]
    public async Task Persisted_MeterListIsServedAfterRestart_UntilItExpires()
    {
        _handler.RespondWithFixture("get_available_meters.json");
        _handler.RespondWithFixture("get_available_meters.json");
        var before = await StartServer().GetAvailableMetersAsync();

        var restarted = StartServer();
        var after = await restarted.GetAvailableMetersAsync();
        _time.Advance(SevCache.MeterListLifetime);
        await restarted.GetAvailableMetersAsync();

        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Persisted_DaysFetchedBeforeALaterPieceFails()
    {
        // 60 days: the newest 31 arrive, the older piece fails. The calls already made mustn't be lost on restart.
        _handler.Respond(UsageJson(DaysOf(1111, Day(-31), 31)));
        _handler.Respond("oops", HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<SevApiException>(() => StartServer().GetHourlyUsageAsync([1111], Start(Day(-60)), Start(Today)));

        var after = await StartServer().GetHourlyUsageAsync([1111], Start(Day(-31)), Start(Today));

        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal(31 * 24, after.Meters.Single().Readings.Count);
    }

    [Fact]
    public async Task Persisted_IncompleteRecentDayIsNotSaved()
    {
        _handler.Respond(UsageJson((1111, Day(-1), 12)));

        await StartServer().GetHourlyUsageAsync([1111], Start(Day(-1)), Start(Today));

        Assert.False(File.Exists(Path.Combine(_cacheDirectory, "UsageReading.json")));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"version": 99, "days": []}""")]
    public async Task Persisted_UnreadableFileIsIgnored(string content)
    {
        Directory.CreateDirectory(_cacheDirectory);
        File.WriteAllText(Path.Combine(_cacheDirectory, "UsageReading.json"), content);
        _handler.Respond(UsageJson((1111, Day(-2), 24)));

        var result = await StartServer().GetHourlyUsageAsync([1111], Start(Day(-2)), Start(Day(-1)));

        Assert.Single(_handler.Requests);
        Assert.Equal(24, result.Meters.Single().Readings.Count);
        Assert.Contains(_log, line => line.StartsWith("Ignoring"));
    }

    [Fact]
    public async Task Persisted_FailedSaveKeepsTheDataInMemory()
    {
        // A file where the directory should be: creating the directory fails.
        Directory.CreateDirectory(Path.GetDirectoryName(_cacheDirectory)!);
        File.WriteAllText(_cacheDirectory, "");
        try
        {
            var server = StartServer();
            _handler.Respond(UsageJson((1111, Day(-2), 24)));

            await server.GetHourlyUsageAsync([1111], Start(Day(-2)), Start(Day(-1)));
            var second = await server.GetHourlyUsageAsync([1111], Start(Day(-2)), Start(Day(-1)));

            Assert.Single(_handler.Requests);
            Assert.Equal(24, second.Meters.Single().Readings.Count);
            Assert.Contains(_log, line => line.StartsWith("Couldn't save"));
        }
        finally
        {
            File.Delete(_cacheDirectory);
        }
    }

    // A new cache loaded from the cache directory, as the server has after a restart.
    private SevDataService StartServer()
    {
        var cache = new SevCache(_time, new SevCacheFiles(_cacheDirectory, _log.Add));
        var client = new SevClient(new HttpClient(_handler, disposeHandler: false) { BaseAddress = SevClient.DefaultBaseAddress });
        return new SevDataService(client, cache);
    }
}
