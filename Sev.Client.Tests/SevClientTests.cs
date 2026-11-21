using System.Net;
using System.Net.Http.Headers;

namespace Sev.Client.Tests;

public class SevClientTests
{
    private const string BaseAddress = "https://api.sev.fo/api/CustomerRESTApi/";

    private readonly FakeHttpMessageHandler _handler = new();
    private readonly SevClient _client;

    public SevClientTests()
    {
        _client = new SevClient(new HttpClient(_handler) { BaseAddress = SevClient.DefaultBaseAddress });
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
    [InlineData(null)]
    [InlineData("https://api.sev.fo/api/CustomerRESTApi")]
    public void Constructor_RejectsBaseAddressWithoutTrailingSlash(string? baseAddress)
    {
        var http = new HttpClient(_handler) { BaseAddress = baseAddress is null ? null : new Uri(baseAddress) };

        Assert.Throws<ArgumentException>(() => new SevClient(http));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task ErrorStatus_ThrowsSevApiExceptionWithStatusAndBody(HttpStatusCode status)
    {
        _handler.Respond("something went wrong", status, "text/plain");

        var ex = await Assert.ThrowsAsync<SevApiException>(() => _client.GetAvailableMetersAsync());

        Assert.Equal(status, ex.StatusCode);
        Assert.Equal("something went wrong", ex.ResponseBody);
        Assert.Contains("something went wrong", ex.Message);
        Assert.Null(ex.RetryAfter);
    }

    [Fact]
    public async Task ErrorStatus_ExposesRetryAfter()
    {
        _handler.Respond("", HttpStatusCode.TooManyRequests).Headers.RetryAfter =
            new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));

        var ex = await Assert.ThrowsAsync<SevApiException>(() => _client.GetAvailableMetersAsync());

        Assert.Equal(TimeSpan.FromSeconds(90), ex.RetryAfter);
    }

    [Fact]
    public async Task LoginAsync_ErrorStatus_ThrowsSevApiException()
    {
        _handler.Respond("invalid credentials", HttpStatusCode.Unauthorized, "text/plain");

        var ex = await Assert.ThrowsAsync<SevApiException>(() => _client.LoginAsync("user@example.com", "wrong"));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    public static TheoryData<DateTime, DateTime> InvalidRanges => new()
    {
        { new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 11) },
        { new DateTime(2026, 10, 10), new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Local) },
        { new DateTime(2026, 10, 10), new DateTime(2026, 10, 10) },
        { new DateTime(2026, 10, 11), new DateTime(2026, 10, 10) },
    };

    [Theory]
    [MemberData(nameof(InvalidRanges))]
    public async Task GetHourlyUsageAsync_RejectsInvalidRangeWithoutCallingApi(DateTime from, DateTime to)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _client.GetHourlyUsageAsync([1111], from, to));

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task CancelledToken_IsPassedToHttpClient()
    {
        _handler.Respond("[]");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.GetAvailableMetersAsync(cts.Token));
    }
}
