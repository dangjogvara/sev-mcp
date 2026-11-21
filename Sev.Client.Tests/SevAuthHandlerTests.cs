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
}
