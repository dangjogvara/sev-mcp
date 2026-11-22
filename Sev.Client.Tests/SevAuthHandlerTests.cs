namespace Sev.Client.Tests;

public class SevAuthHandlerTests
{
    private readonly FakeHttpMessageHandler _loginHandler = new();
    private readonly FakeHttpMessageHandler _apiHandler = new();
    private readonly SevClient _client;

    public SevAuthHandlerTests()
    {
        var tokens = new SevTokenProvider(
            new HttpClient(_loginHandler) { BaseAddress = SevClient.DefaultBaseAddress },
            "user@example.com",
            "secret");
        var http = new HttpClient(new SevAuthHandler(tokens) { InnerHandler = _apiHandler })
        {
            BaseAddress = SevClient.DefaultBaseAddress
        };
        _client = new SevClient(http);
    }

    [Fact]
    public async Task EveryRequestGetsBearerToken_WithOneLogin()
    {
        _loginHandler.Respond("eyJtoken", mediaType: "text/plain");
        _apiHandler.Respond("[]");
        _apiHandler.Respond("[]");

        await _client.GetAvailableMetersAsync();
        await _client.GetHourlyUsageAsync([1111], new DateTime(2026, 10, 10), new DateTime(2026, 10, 11));

        Assert.Single(_loginHandler.Requests);
        Assert.Equal(2, _apiHandler.Requests.Count);
        Assert.All(_apiHandler.Requests, r => Assert.Equal("Bearer eyJtoken", r.Authorization));
    }

    [Fact]
    public async Task FailedLogin_DoesNotCallApi()
    {
        _loginHandler.Respond("invalid credentials", System.Net.HttpStatusCode.Unauthorized, "text/plain");

        await Assert.ThrowsAsync<SevApiException>(() => _client.GetAvailableMetersAsync());

        Assert.Empty(_apiHandler.Requests);
    }

    [Fact]
    public async Task Unauthorized_LogsInAgainAndRetriesOnce()
    {
        _loginHandler.Respond("old", mediaType: "text/plain");
        _loginHandler.Respond("new", mediaType: "text/plain");
        _apiHandler.Respond("", System.Net.HttpStatusCode.Unauthorized);
        _apiHandler.Respond("[]");

        await _client.GetHourlyUsageAsync([1111], new DateTime(2026, 10, 10), new DateTime(2026, 10, 11));

        Assert.Equal(2, _loginHandler.Requests.Count);
        Assert.Equal(2, _apiHandler.Requests.Count);
        Assert.Equal("Bearer old", _apiHandler.Requests[0].Authorization);
        Assert.Equal("Bearer new", _apiHandler.Requests[1].Authorization);
        // The retry is a copy of the original request, body included.
        Assert.Equal(_apiHandler.Requests[0].Uri, _apiHandler.Requests[1].Uri);
        Assert.Equal(_apiHandler.Requests[0].Body, _apiHandler.Requests[1].Body);
    }

    [Fact]
    public async Task Unauthorized_Twice_GivesUpAfterOneRetry()
    {
        _loginHandler.Respond("old", mediaType: "text/plain");
        _loginHandler.Respond("new", mediaType: "text/plain");
        _apiHandler.Respond("", System.Net.HttpStatusCode.Unauthorized);
        _apiHandler.Respond("still not allowed", System.Net.HttpStatusCode.Unauthorized, "text/plain");

        var ex = await Assert.ThrowsAsync<SevApiException>(() => _client.GetAvailableMetersAsync());

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal(2, _loginHandler.Requests.Count);
        Assert.Equal(2, _apiHandler.Requests.Count);
    }

    [Fact]
    public async Task OtherErrors_AreNotRetried()
    {
        _loginHandler.Respond("eyJtoken", mediaType: "text/plain");
        _apiHandler.Respond("bad request", System.Net.HttpStatusCode.BadRequest, "text/plain");

        await Assert.ThrowsAsync<SevApiException>(() => _client.GetAvailableMetersAsync());

        Assert.Single(_loginHandler.Requests);
        Assert.Single(_apiHandler.Requests);
    }

    [Fact]
    public async Task Unauthorized_WhenReloginFails_DoesNotRetryApi()
    {
        _loginHandler.Respond("old", mediaType: "text/plain");
        _loginHandler.Respond("invalid credentials", System.Net.HttpStatusCode.Unauthorized, "text/plain");
        _apiHandler.Respond("", System.Net.HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<SevApiException>(() => _client.GetAvailableMetersAsync());

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal(2, _loginHandler.Requests.Count);
        Assert.Single(_apiHandler.Requests);
    }
}
