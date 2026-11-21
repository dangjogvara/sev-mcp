using System.Net.Http.Json;

namespace Sev.Client;

// Logs in to SEV and hands out the token to every request, so the rate limit
// (10 calls per 5 minutes, login included) isn't spent on logins. Register as a singleton.
// SEV tokens expire after 4 hours; the provider logs in again shortly before that, and
// SevAuthHandler calls Invalidate when SEV rejects a token anyway.
// It logs in again rather than using refresh_jwt_key_token: both cost one rate-limited call,
// refresh only works while the token is still valid, and one code path is simpler.
public class SevTokenProvider
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(4);
    public static readonly TimeSpan RenewalMargin = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly string _userName;
    private readonly string _apiKey;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Token and the time it was obtained, replaced together as one object so readers never see a mix.
    private TokenEntry? _current;

    // The HttpClient is only used for login, so it must not go through SevAuthHandler.
    public SevTokenProvider(HttpClient http, string userName, string apiKey, TimeProvider? timeProvider = null)
    {
        SevClient.ValidateBaseAddress(http);
        _http = http;
        _userName = userName;
        _apiKey = apiKey;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (IsUsable(_current))
        {
            return _current!.Token;
        }

        // Only one caller logs in; the others wait here and then reuse its token.
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!IsUsable(_current))
            {
                var token = await LoginAsync(cancellationToken);
                _current = new TokenEntry(token, _time.GetUtcNow());
            }
            return _current!.Token;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Forget a token SEV has rejected, so the next GetTokenAsync logs in again. Does nothing if the
    // token has already been replaced, so several requests failing with the same token cause one login.
    public void Invalidate(string token)
    {
        var current = _current;
        if (current?.Token == token)
        {
            Interlocked.CompareExchange(ref _current, null, current);
        }
    }

    private bool IsUsable(TokenEntry? entry) =>
        entry is not null && _time.GetUtcNow() - entry.ObtainedAt < TokenLifetime - RenewalMargin;

    private async Task<string> LoginAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(
            "login_and_get_jwt_token",
            new { UserName = _userName, Password = _apiKey },
            SevClient.JsonOptions,
            cancellationToken
        );
        await SevClient.EnsureSuccessAsync(response, cancellationToken);

        var token = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        return token.Length > 0
            ? token
            : throw new InvalidOperationException("SEV login succeeded but returned an empty token.");
    }

    private sealed record TokenEntry(string Token, DateTimeOffset ObtainedAt);
}
