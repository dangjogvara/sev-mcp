using System.Net;

namespace MyUplink.Client.Tests;

public class MyUplinkTokenProviderTests
{
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly FakeTimeProvider _time = new();
    private readonly MyUplinkTokenProvider _tokens;

    public MyUplinkTokenProviderTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = MyUplinkClient.DefaultBaseAddress };
        _tokens = new MyUplinkTokenProvider(http, "client-id", "client-secret", _time);
    }

    [Fact]
    public async Task Login_SendsClientCredentialsWithReadOnlyScope()
    {
        _handler.RespondWithToken("abc");

        var token = await _tokens.GetTokenAsync();

        Assert.Equal("abc", token);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.myuplink.com/oauth/token", request.Uri.ToString());
        Assert.Equal("grant_type=client_credentials&client_id=client-id&client_secret=client-secret&scope=READSYSTEM", request.Body);
        Assert.Equal(MyUplinkClient.UserAgent, request.UserAgent);
    }

    [Fact]
    public async Task Token_IsReusedUntilShortlyBeforeItExpires()
    {
        _handler.RespondWithToken("first", expiresIn: 3600);
        _handler.RespondWithToken("second");

        var first = await _tokens.GetTokenAsync();
        _time.Advance(TimeSpan.FromHours(1) - MyUplinkTokenProvider.RenewalMargin - TimeSpan.FromSeconds(1));
        var stillFirst = await _tokens.GetTokenAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        var second = await _tokens.GetTokenAsync();

        Assert.Equal("first", first);
        Assert.Equal("first", stillFirst);
        Assert.Equal("second", second);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Invalidate_LogsInAgainOnlyForTheCurrentToken()
    {
        _handler.RespondWithToken("first");
        _handler.RespondWithToken("second");

        await _tokens.GetTokenAsync();
        _tokens.Invalidate("some-older-token");
        Assert.Equal("first", await _tokens.GetTokenAsync());
        _tokens.Invalidate("first");

        Assert.Equal("second", await _tokens.GetTokenAsync());
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task RejectedLogin_ThrowsWithTheStatus()
    {
        _handler.Respond("""{"error":"invalid_client"}""", HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<MyUplinkApiException>(() => _tokens.GetTokenAsync());

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("invalid_client", ex.ResponseBody);
    }

    [Fact]
    public async Task LoginWithoutToken_Throws()
    {
        _handler.Respond("""{"expires_in":3600}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _tokens.GetTokenAsync());
    }

    [Fact]
    public void Constructor_RejectsBaseAddressWithoutTrailingSlash()
    {
        // A bare host gets its slash from Uri itself, so the case to catch is a path without one.
        var http = new HttpClient(_handler) { BaseAddress = new Uri("https://api.myuplink.com/v2") };

        Assert.Throws<ArgumentException>(() => new MyUplinkTokenProvider(http, "id", "secret"));
    }
}
