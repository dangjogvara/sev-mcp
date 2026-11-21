using System.Net;

namespace Sev.Client.Tests;

public class SevTokenProviderTests
{
    private readonly FakeHttpMessageHandler _loginHandler = new();
    private readonly SevTokenProvider _tokens;

    public SevTokenProviderTests()
    {
        _tokens = new SevTokenProvider(
            new HttpClient(_loginHandler) { BaseAddress = SevClient.DefaultBaseAddress },
            "user@example.com",
            "secret");
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
}
