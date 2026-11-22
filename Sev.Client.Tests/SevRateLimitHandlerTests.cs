namespace Sev.Client.Tests;

public class SevRateLimitHandlerTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeHttpMessageHandler _loginHandler = new();
    private readonly FakeHttpMessageHandler _apiHandler = new();
    private readonly SevClient _client;

    public SevRateLimitHandlerTests()
    {
        // Wired like Sev.McpServer: one limiter shared by the login client and the API client.
        var limiter = new SevRateLimiter(_time, maxCalls: 3);
        var tokens = new SevTokenProvider(
            new HttpClient(new SevRateLimitHandler(limiter) { InnerHandler = _loginHandler })
            {
                BaseAddress = SevClient.DefaultBaseAddress
            },
            "user@example.com",
            "secret");
        var http = new HttpClient(new SevAuthHandler(tokens)
        {
            InnerHandler = new SevRateLimitHandler(limiter) { InnerHandler = _apiHandler }
        })
        {
            BaseAddress = SevClient.DefaultBaseAddress
        };
        _client = new SevClient(http);

        _loginHandler.Respond("eyJtoken", mediaType: "text/plain");
    }

    [Fact]
    public async Task LoginCountsTowardsTheLimit()
    {
        _apiHandler.Respond("[]");
        _apiHandler.Respond("[]");

        await _client.GetAvailableMetersAsync();   // login + 1
        await _client.GetAvailableMetersAsync();   // 1

        var ex = await Assert.ThrowsAsync<SevRateLimitExceededException>(() => _client.GetAvailableMetersAsync());

        Assert.Equal(TimeSpan.FromMinutes(5), ex.RetryAfter);
        Assert.Contains("300 seconds", ex.Message);
        Assert.Single(_loginHandler.Requests);
        Assert.Equal(2, _apiHandler.Requests.Count);
    }

    [Fact]
    public async Task RefusedRequestNeverReachesTheNetwork()
    {
        _apiHandler.Respond("[]");
        _apiHandler.Respond("[]");
        await _client.GetAvailableMetersAsync();
        await _client.GetAvailableMetersAsync();

        await Assert.ThrowsAsync<SevRateLimitExceededException>(() => _client.GetAvailableMetersAsync());
        await Assert.ThrowsAsync<SevRateLimitExceededException>(() => _client.GetAvailableMetersAsync());

        Assert.Equal(2, _apiHandler.Requests.Count);
    }

    [Fact]
    public async Task CancelledRequest_DoesNotUseACall()
    {
        // The handler on its own, with room for one call.
        var limiter = new SevRateLimiter(_time, maxCalls: 1);
        var network = new FakeHttpMessageHandler();
        network.Respond("[]");
        using var invoker = new HttpMessageInvoker(new SevRateLimitHandler(limiter) { InnerHandler = network });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://example.com/"), new CancellationToken(canceled: true)));
        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://example.com/"), CancellationToken.None);

        Assert.Single(network.Requests);
        Assert.False(limiter.TryAcquire(out _));
    }

    [Fact]
    public async Task AllowedAgainAfterWindow()
    {
        _apiHandler.Respond("[]");
        _apiHandler.Respond("[]");
        _apiHandler.Respond("[]");
        await _client.GetAvailableMetersAsync();
        await _client.GetAvailableMetersAsync();

        _time.Advance(TimeSpan.FromMinutes(5));

        await _client.GetAvailableMetersAsync();
        Assert.Equal(3, _apiHandler.Requests.Count);
    }
}
