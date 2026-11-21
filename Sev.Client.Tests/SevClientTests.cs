using System.Net;

namespace Sev.Client.Tests;

public class SevClientTests
{
    private const string BaseAddress = "https://api.sev.fo/api/CustomerRESTApi/";

    private readonly FakeHttpMessageHandler _handler = new();
    private readonly SevClient _client;

    public SevClientTests()
    {
        _client = new SevClient(new HttpClient(_handler) { BaseAddress = new Uri(BaseAddress) });
    }

    [Fact]
    public async Task LoginAsync_PostsSnakeCaseCredentials()
    {
        _handler.Respond("eyJtoken", mediaType: "text/plain");

        await _client.LoginAsync("user@example.com", "secret");

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(BaseAddress + "login_and_get_jwt_token", request.Uri.ToString());
        Assert.Equal("""{"user_name":"user@example.com","password":"secret"}""", request.Body);
    }

    [Fact]
    public async Task LoginAsync_SendsTokenAsBearerOnLaterRequests()
    {
        _handler.Respond("eyJtoken", mediaType: "text/plain");
        _handler.Respond("[]");

        await _client.LoginAsync("user@example.com", "secret");
        await _client.GetAvailableMetersAsync();

        Assert.Equal("Bearer eyJtoken", _handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task GetAvailableMetersAsync_PostsToEndpoint()
    {
        _handler.Respond("[]");

        await _client.GetAvailableMetersAsync();

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(BaseAddress + "get_available_meters", request.Uri.ToString());
    }

    [Fact]
    public async Task GetAvailableMetersAsync_MapsAllFields()
    {
        _handler.RespondWithFixture("get_available_meters.json");

        var customers = await _client.GetAvailableMetersAsync();

        var customer = Assert.Single(customers);
        Assert.Equal("Test Customer", customer.CustomerName);

        var installation = Assert.Single(customer.Installations);
        Assert.Equal(new DateTime(2019, 6, 1), installation.MoveInDate);
        Assert.Null(installation.MoveOnDate);
        Assert.Equal(10001, installation.InstId);
        Assert.Null(installation.InstNickname);
        Assert.Equal("Testgøta 1 ", installation.Address);
        Assert.Null(installation.Floor);
        Assert.Equal("100", installation.PostalCode);
        Assert.Equal("Tórshavn", installation.PostalDistrict);
        Assert.Equal(310, installation.TarifNumber);
        Assert.Equal("OS", installation.BillingType);
        Assert.Equal(2, installation.Meters.Count);

        var meter = installation.Meters[0];
        Assert.Equal(1111, meter.MeterId);
        Assert.Null(meter.MeterNickname);
        Assert.Equal("11111111", meter.SerialNumber);
        Assert.Equal("E350 ZMF2AD2-0560", meter.MeterName);
        Assert.Equal("E-01", meter.MeterType);
        Assert.Equal(new DateOnly(2013, 3, 12), meter.InstallDate);
        Assert.Null(meter.RemovalDate);
    }

    [Fact]
    public async Task GetHourlyUsageAsync_PostsRequestWithLocalDates()
    {
        _handler.Respond("[]");

        await _client.GetHourlyUsageAsync([1111, 2222], new DateTime(2026, 10, 10), new DateTime(2026, 10, 11));

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(BaseAddress + "hourly_kwh_usage", request.Uri.ToString());
        Assert.Equal(
            """{"meters":[1111,2222],"from_date":"2026-10-10T00:00:00","to_date":"2026-10-11T00:00:00"}""",
            request.Body);
    }

    [Fact]
    public async Task GetHourlyUsageAsync_MapsAllFields()
    {
        _handler.RespondWithFixture("hourly_kwh_usage.json");

        var usage = await _client.GetHourlyUsageAsync([1111, 2222], new DateTime(2026, 10, 10), new DateTime(2026, 10, 11));

        Assert.Equal(2, usage.Count);
        var meter = usage[0];
        Assert.Equal("2222", meter.MeterId);
        Assert.Equal(3, meter.Readings.Count);

        var reading = meter.Readings[1];
        Assert.Equal(new DateTime(2026, 10, 10, 1, 0, 0), reading.TimeStamp);
        Assert.Equal(DateTimeKind.Unspecified, reading.TimeStamp.Kind);
        Assert.Equal(0.7539999999971769m, reading.Value);
        Assert.Equal(18184.887m, reading.CumulativeValue);
        Assert.Equal("kWh", reading.Unit);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task ErrorStatus_Throws(HttpStatusCode status)
    {
        _handler.Respond("", status);

        await Assert.ThrowsAsync<HttpRequestException>(() => _client.GetAvailableMetersAsync());
    }
}
