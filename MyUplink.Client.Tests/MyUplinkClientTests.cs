using System.Net;

namespace MyUplink.Client.Tests;

// Fixtures/*.json are sanitized copies of real myUplink answers (made-up IDs and values, same structure).
public class MyUplinkClientTests
{
    private readonly FakeHttpMessageHandler _login = new();
    private readonly FakeHttpMessageHandler _api = new();
    private readonly MyUplinkClient _client;

    public MyUplinkClientTests()
    {
        var tokens = new MyUplinkTokenProvider(
            new HttpClient(_login) { BaseAddress = MyUplinkClient.DefaultBaseAddress }, "client-id", "client-secret", new FakeTimeProvider());
        _client = new MyUplinkClient(new HttpClient(_api) { BaseAddress = MyUplinkClient.DefaultBaseAddress }, tokens);
        _login.RespondWithToken("token-1");
    }

    [Fact]
    public async Task GetSystems_ReadsSystemsAndDevices()
    {
        _api.RespondWithFixture("systems_me.json");

        var system = Assert.Single(await _client.GetSystemsAsync());

        Assert.Equal("00000000-0000-0000-0000-000000000001", system.SystemId);
        Assert.Equal("Test heat pump", system.Name);
        Assert.False(system.HasAlarm);
        var device = Assert.Single(system.Devices!);
        Assert.Equal("TEST000000000001", device.Id);
        Assert.Equal("Connected", device.ConnectionState);
        Assert.Equal("CTC EcoZenith i360", device.Product?.Name);
    }

    [Fact]
    public async Task Requests_CarryTokenUserAgentAndEnglish()
    {
        _api.RespondWithFixture("systems_me.json");

        await _client.GetSystemsAsync();

        var request = Assert.Single(_api.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.myuplink.com/v2/systems/me?page=1&itemsPerPage=100", request.Uri.ToString());
        Assert.Equal("Bearer token-1", request.Authorization);
        Assert.Equal(MyUplinkClient.UserAgent, request.UserAgent);
        Assert.Equal("en-GB", request.AcceptLanguage);
    }

    [Fact]
    public async Task GetPoints_ReadsValuesLabelsAndSettings()
    {
        _api.RespondWithFixture("device_points.json");

        var points = await _client.GetPointsAsync("TEST000000000001");

        Assert.Equal("https://api.myuplink.com/v2/devices/TEST000000000001/points", _api.Requests[0].Uri.ToString());
        var outdoor = points.Single(p => p.ParameterId == "62000");
        Assert.Equal("Outdoor Temperature", outdoor.ParameterName);
        Assert.Equal("General", outdoor.Category);
        Assert.Equal(3.5, outdoor.Value);
        Assert.Equal("°C", outdoor.ParameterUnit);
        Assert.False(outdoor.Writable);
        var status = points.Single(p => p.ParameterId == "62005");
        Assert.Equal("HC", status.StrVal);
        Assert.Contains(status.EnumValues!, e => e.Value == "4" && e.Text == "HC");
        Assert.True(points.Single(p => p.ParameterId == "61502").Writable);
    }

    [Fact]
    public async Task GetPoints_AcceptsNumericParameterIds()
    {
        _api.Respond("""[{"category":"General","parameterId":62000,"parameterName":"Outdoor Temperature","writable":false,"value":1.5}]""");

        var point = Assert.Single(await _client.GetPointsAsync("TEST000000000001"));

        Assert.Equal("62000", point.ParameterId);
    }

    [Fact]
    public async Task GetActiveAlarms_ReadsAlarms()
    {
        _api.RespondWithFixture("notifications_active.json");

        var alarm = Assert.Single(await _client.GetActiveAlarmsAsync("00000000-0000-0000-0000-000000000001"));

        Assert.Equal(
            "https://api.myuplink.com/v2/systems/00000000-0000-0000-0000-000000000001/notifications/active?page=1&itemsPerPage=100",
            _api.Requests[0].Uri.ToString());
        Assert.Equal(15, alarm.AlarmNumber);
        Assert.Equal("Low flow", alarm.Header);
        Assert.Equal("Check the heating circuit.", alarm.Description);
    }

    [Fact]
    public async Task RejectedToken_LogsInAgainAndRetriesOnce()
    {
        _api.Respond("", HttpStatusCode.Unauthorized);
        _login.RespondWithToken("token-2");
        _api.RespondWithFixture("systems_me.json");

        Assert.Single(await _client.GetSystemsAsync());

        Assert.Equal(2, _login.Requests.Count);
        Assert.Equal(["Bearer token-1", "Bearer token-2"], _api.Requests.Select(r => r.Authorization));
    }

    [Fact]
    public async Task SecondRejection_Throws()
    {
        _api.Respond("", HttpStatusCode.Unauthorized);
        _login.RespondWithToken("token-2");
        _api.Respond("", HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<MyUplinkApiException>(() => _client.GetSystemsAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal(2, _api.Requests.Count);
    }

    [Fact]
    public async Task RateLimited_ThrowsWithTimeFromRateLimitReset()
    {
        _api.Respond("", HttpStatusCode.TooManyRequests).Headers.Add("ratelimit-reset", "30");

        var ex = await Assert.ThrowsAsync<MyUplinkApiException>(() => _client.GetSystemsAsync());

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
    }
}
