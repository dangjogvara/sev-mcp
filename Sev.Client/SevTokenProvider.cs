using System.Net.Http.Json;

namespace Sev.Client;

// Logs in to SEV once and hands out the token to every request, so the rate limit
// (10 calls per 5 minutes, login included) isn't spent on logins. Register as a singleton.
public class SevTokenProvider
{
    private readonly HttpClient _http;
    private readonly string _userName;
    private readonly string _apiKey;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;

    // The HttpClient is only used for login, so it must not go through SevAuthHandler.
    public SevTokenProvider(HttpClient http, string userName, string apiKey)
    {
        SevClient.ValidateBaseAddress(http);
        _http = http;
        _userName = userName;
        _apiKey = apiKey;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_token is not null)
        {
            return _token;
        }

        // Only one caller logs in; the others wait here and then reuse its token.
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _token ??= await LoginAsync(cancellationToken);
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

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
}
