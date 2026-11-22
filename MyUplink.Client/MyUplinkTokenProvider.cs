using System.Net.Http.Json;
using MyUplink.Client.Models;

namespace MyUplink.Client;

// Logs in to myUplink with the application's client ID and secret (OAuth2 client credentials, read-only
// scope) and hands out the token to every request until shortly before it expires (myUplink: 1 hour).
// There's no refresh token, so it simply logs in again. Register as a singleton so all requests share one token.
public class MyUplinkTokenProvider
{
    public static readonly TimeSpan RenewalMargin = TimeSpan.FromMinutes(5);

    // Read-only: the tools never change the heat pump's settings.
    public const string Scope = "READSYSTEM";

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Token and its expiry, replaced together as one object so readers never see a mix.
    private TokenEntry? _current;

    public MyUplinkTokenProvider(HttpClient http, string clientId, string clientSecret, TimeProvider? timeProvider = null)
    {
        MyUplinkClient.ValidateBaseAddress(http);
        _http = http;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var current = Volatile.Read(ref _current);
        if (IsUsable(current))
        {
            return current!.Token;
        }

        // Only one caller logs in; the others wait here and then reuse its token.
        await _lock.WaitAsync(cancellationToken);
        try
        {
            current = Volatile.Read(ref _current);
            if (!IsUsable(current))
            {
                current = await LoginAsync(cancellationToken);
                Volatile.Write(ref _current, current);
            }
            return current!.Token;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Forget a token myUplink has rejected, so the next GetTokenAsync logs in again. Does nothing if the
    // token has already been replaced.
    public void Invalidate(string token)
    {
        var current = Volatile.Read(ref _current);
        if (current?.Token == token)
        {
            Interlocked.CompareExchange(ref _current, null, current);
        }
    }

    private bool IsUsable(TokenEntry? entry) =>
        entry is not null && _time.GetUtcNow() < entry.ExpiresAt - RenewalMargin;

    private async Task<TokenEntry> LoginAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["scope"] = Scope,
            }),
        };
        request.Headers.UserAgent.ParseAdd(MyUplinkClient.UserAgent);
        var obtainedAt = _time.GetUtcNow();

        using var response = await _http.SendAsync(request, cancellationToken);
        await MyUplinkApiException.ThrowIfFailedAsync(response, cancellationToken);

        var answer = await response.Content.ReadFromJsonAsync<TokenResponse>(MyUplinkClient.JsonOptions, cancellationToken);
        if (string.IsNullOrWhiteSpace(answer?.AccessToken))
        {
            throw new InvalidOperationException("myUplink login succeeded but returned no access token.");
        }
        return new TokenEntry(answer.AccessToken, obtainedAt + TimeSpan.FromSeconds(answer.ExpiresIn));
    }

    private sealed record TokenEntry(string Token, DateTimeOffset ExpiresAt);
}
