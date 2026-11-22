using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MyUplink.Client.Models;

namespace MyUplink.Client;

// Reads heat pump data from myUplink's public API (NIBE, CTC and other brands report there). Read-only.
// The HttpClient's BaseAddress must be DefaultBaseAddress; the token comes from the shared MyUplinkTokenProvider.
// myUplink allows 25 calls per minute; a refusal surfaces as MyUplinkApiException with RetryAfter.
public class MyUplinkClient
{
    public static readonly Uri DefaultBaseAddress = new("https://api.myuplink.com/");

    // myUplink sits behind Cloudflare, which refuses some default client signatures (error 1010), so every
    // request names the client.
    public const string UserAgent = "sev-mcp/1.0";

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly MyUplinkTokenProvider _tokens;

    public MyUplinkClient(HttpClient http, MyUplinkTokenProvider tokens)
    {
        ValidateBaseAddress(http);
        _http = http;
        _tokens = tokens;
    }

    // The account's systems (installations) and their devices.
    public async Task<IReadOnlyList<MyUplinkSystem>> GetSystemsAsync(CancellationToken cancellationToken = default) =>
        (await GetAsync<SystemsPage>("v2/systems/me?page=1&itemsPerPage=100", cancellationToken))?.Systems ?? [];

    // Every parameter of a device with its current value, names in English.
    public async Task<IReadOnlyList<DataPoint>> GetPointsAsync(string deviceId, CancellationToken cancellationToken = default) =>
        await GetAsync<List<DataPoint>>($"v2/devices/{Uri.EscapeDataString(deviceId)}/points", cancellationToken) ?? [];

    // The alarms that are active now in a system.
    public async Task<IReadOnlyList<MyUplinkAlarm>> GetActiveAlarmsAsync(string systemId, CancellationToken cancellationToken = default) =>
        (await GetAsync<AlarmsPage>(
            $"v2/systems/{Uri.EscapeDataString(systemId)}/notifications/active?page=1&itemsPerPage=100", cancellationToken))
        ?.Notifications ?? [];

    // Sends a GET with the token. If myUplink rejects the token (401), logs in again and retries once.
    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        var token = await _tokens.GetTokenAsync(cancellationToken);
        var response = await SendAsync(path, token, cancellationToken);
        try
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                _tokens.Invalidate(token);
                response = await SendAsync(path, await _tokens.GetTokenAsync(cancellationToken), cancellationToken);
            }
            await MyUplinkApiException.ThrowIfFailedAsync(response, cancellationToken);
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        }
        finally
        {
            response.Dispose();
        }
    }

    private Task<HttpResponseMessage> SendAsync(string path, string token, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.AcceptLanguage.ParseAdd("en-GB");
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return _http.SendAsync(request, cancellationToken);
    }

    // Without the trailing slash, "v2/..." would resolve against the wrong path.
    internal static void ValidateBaseAddress(HttpClient http)
    {
        if (http.BaseAddress is null || !http.BaseAddress.AbsoluteUri.EndsWith('/'))
        {
            throw new ArgumentException(
                $"HttpClient.BaseAddress must be set and end with '/', e.g. {DefaultBaseAddress}", nameof(http));
        }
    }
}
