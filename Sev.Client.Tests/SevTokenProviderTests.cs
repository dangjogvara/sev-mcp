using System.Net;
using System.Text;

namespace Sev.Client.Tests;

public class SevTokenProviderTests
{
    private readonly FakeHttpMessageHandler _loginHandler = new();
    private readonly FakeTimeProvider _time = new();
    private readonly SevTokenProvider _tokens;

    public SevTokenProviderTests()
    {
        _tokens = new SevTokenProvider(
            new HttpClient(_loginHandler) { BaseAddress = SevClient.DefaultBaseAddress },
            "user@example.com",
            "secret",
            _time);
    }

    [Fact]
    public async Task GetTokenAsync_PostsSnakeCaseCredentials()
    {
        _loginHandler.Respond("eyJtoken", mediaType: "text/plain");

        var token = await _tokens.GetTokenAsync();

        Assert.Equal("eyJtoken", token);
        var request = Assert.Single(_loginHandler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.sev.fo/api/CustomerRESTApi/login_and_get_jwt_token", request.Uri.ToString());
        Assert.Equal("""{"user_name":"user@example.com","password":"secret"}""", request.Body);
    }

    [Fact]
    public async Task GetTokenAsync_LogsInOnlyOnce()
    {
        _loginHandler.Respond("eyJtoken", mediaType: "text/plain");

        await _tokens.GetTokenAsync();
        await _tokens.GetTokenAsync();
        var token = await _tokens.GetTokenAsync();

        Assert.Equal("eyJtoken", token);
        Assert.Single(_loginHandler.Requests);
    }

    [Fact]
    public async Task GetTokenAsync_ConcurrentCallersShareOneLogin()
    {
        var gate = new TaskCompletionSource();
        _loginHandler.Gate = gate.Task;
        _loginHandler.Respond("eyJtoken", mediaType: "text/plain");

        var calls = Enumerable.Range(0, 10).Select(_ => _tokens.GetTokenAsync()).ToList();
        gate.SetResult();
        var tokens = await Task.WhenAll(calls);

        Assert.All(tokens, t => Assert.Equal("eyJtoken", t));
        Assert.Single(_loginHandler.Requests);
    }

    [Fact]
    public async Task GetTokenAsync_TrimsWhitespace()
    {
        _loginHandler.Respond("eyJtoken\n", mediaType: "text/plain");

        Assert.Equal("eyJtoken", await _tokens.GetTokenAsync());
    }

    [Fact]
    public async Task GetTokenAsync_RejectsEmptyToken()
    {
        _loginHandler.Respond("", mediaType: "text/plain");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _tokens.GetTokenAsync());
    }

    [Fact]
    public async Task GetTokenAsync_FailedLoginIsNotCached()
    {
        _loginHandler.Respond("invalid credentials", HttpStatusCode.Unauthorized, "text/plain");
        _loginHandler.Respond("eyJtoken", mediaType: "text/plain");

        var ex = await Assert.ThrowsAsync<SevApiException>(() => _tokens.GetTokenAsync());
        var token = await _tokens.GetTokenAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal("eyJtoken", token);
        Assert.Equal(2, _loginHandler.Requests.Count);
    }

    [Fact]
    public void Constructor_RejectsBaseAddressWithoutTrailingSlash()
    {
        var http = new HttpClient(_loginHandler) { BaseAddress = new Uri("https://api.sev.fo/api/CustomerRESTApi") };

        Assert.Throws<ArgumentException>(() => new SevTokenProvider(http, "user", "key"));
    }

    [Fact]
    public async Task GetTokenAsync_ReusesTokenUntilShortlyBeforeExpiry()
    {
        _loginHandler.Respond("first", mediaType: "text/plain");

        await _tokens.GetTokenAsync();
        _time.Advance(SevTokenProvider.TokenLifetime - SevTokenProvider.RenewalMargin - TimeSpan.FromSeconds(1));
        var token = await _tokens.GetTokenAsync();

        Assert.Equal("first", token);
        Assert.Single(_loginHandler.Requests);
    }

    [Fact]
    public async Task GetTokenAsync_LogsInAgainShortlyBeforeExpiry()
    {
        _loginHandler.Respond("first", mediaType: "text/plain");
        _loginHandler.Respond("second", mediaType: "text/plain");

        await _tokens.GetTokenAsync();
        _time.Advance(SevTokenProvider.TokenLifetime - SevTokenProvider.RenewalMargin);
        var renewed = await _tokens.GetTokenAsync();
        _time.Advance(TimeSpan.FromHours(1));
        var later = await _tokens.GetTokenAsync();

        Assert.Equal("second", renewed);
        Assert.Equal("second", later);
        Assert.Equal(2, _loginHandler.Requests.Count);
    }

    [Fact]
    public async Task GetTokenAsync_ConcurrentCallersShareOneRenewal()
    {
        _loginHandler.Respond("first", mediaType: "text/plain");
        await _tokens.GetTokenAsync();
        _time.Advance(SevTokenProvider.TokenLifetime);

        var gate = new TaskCompletionSource();
        _loginHandler.Gate = gate.Task;
        _loginHandler.Respond("second", mediaType: "text/plain");
        var calls = Enumerable.Range(0, 10).Select(_ => _tokens.GetTokenAsync()).ToList();
        gate.SetResult();
        var tokens = await Task.WhenAll(calls);

        Assert.All(tokens, t => Assert.Equal("second", t));
        Assert.Equal(2, _loginHandler.Requests.Count);
    }

    [Fact]
    public async Task Invalidate_NextCallLogsInAgain()
    {
        _loginHandler.Respond("first", mediaType: "text/plain");
        _loginHandler.Respond("second", mediaType: "text/plain");

        var first = await _tokens.GetTokenAsync();
        _tokens.Invalidate(first);
        var second = await _tokens.GetTokenAsync();

        Assert.Equal("second", second);
        Assert.Equal(2, _loginHandler.Requests.Count);
    }

    [Fact]
    public async Task Invalidate_OldTokenDoesNotDiscardNewerOne()
    {
        _loginHandler.Respond("first", mediaType: "text/plain");
        _loginHandler.Respond("second", mediaType: "text/plain");

        var first = await _tokens.GetTokenAsync();
        _tokens.Invalidate(first);
        await _tokens.GetTokenAsync();
        // A second request that also failed with the old token must not cause another login.
        _tokens.Invalidate(first);
        var token = await _tokens.GetTokenAsync();

        Assert.Equal("second", token);
        Assert.Equal(2, _loginHandler.Requests.Count);
    }

    [Fact]
    public async Task GetTokenAsync_ConcurrentWithInvalidate_NeverThrowsNullReference()
    {
        var handler = new AlwaysSucceedLoginHandler();
        using var http = new HttpClient(handler) { BaseAddress = SevClient.DefaultBaseAddress };
        var provider = new SevTokenProvider(http, "user@example.com", "secret", _time);

        var workers = Enumerable.Range(0, 16).Select(worker => Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                var token = await provider.GetTokenAsync();
                Assert.False(string.IsNullOrWhiteSpace(token));
                if ((i + worker) % 5 == 0)
                {
                    provider.Invalidate(token);
                }
            }
        }));

        await Task.WhenAll(workers);
    }

    private sealed class AlwaysSucceedLoginHandler : HttpMessageHandler
    {
        private int _count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"token-{Interlocked.Increment(ref _count)}", Encoding.UTF8, "text/plain")
            });
    }
}
