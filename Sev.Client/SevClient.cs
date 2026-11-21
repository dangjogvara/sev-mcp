using System.Net.Http.Json;
using System.Text.Json;
using Sev.Client.Models;

namespace Sev.Client;

public class SevClient
{
    public static readonly Uri DefaultBaseAddress = new("https://api.sev.fo/api/CustomerRESTApi/");

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _http;

    // The HttpClient must send a Bearer token on every request, normally through SevAuthHandler.
    public SevClient(HttpClient http)
    {
        ValidateBaseAddress(http);
        _http = http;
    }

    public async Task<IReadOnlyList<Customer>> GetAvailableMetersAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync("get_available_meters", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var customers = await response.Content.ReadFromJsonAsync<List<Customer>>(JsonOptions, cancellationToken);
        return customers ?? [];
    }

    public async Task<IReadOnlyList<MeterUsage>> GetHourlyUsageAsync(
        IReadOnlyList<int> meterIds, DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        ValidateRange(from, to);
        var request = new UsageRequest(meterIds, from, to);

        using var response = await _http.PostAsJsonAsync("hourly_kwh_usage", request, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var usage = await response.Content.ReadFromJsonAsync<List<MeterUsage>>(JsonOptions, cancellationToken);
        return usage ?? [];
    }

    // The API works in local Faroese time and serializes DateTime as-is, so a Utc or Local
    // DateTime would be sent with "Z" or an offset and shift the requested hours.
    private static void ValidateRange(DateTime from, DateTime to)
    {
        if (from.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException("Must be local Faroese time with DateTimeKind.Unspecified.", nameof(from));
        }
        if (to.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException("Must be local Faroese time with DateTimeKind.Unspecified.", nameof(to));
        }
        if (from >= to)
        {
            throw new ArgumentException("Must be before 'to'; the range is [from, to).", nameof(from));
        }
    }

    // Without the trailing slash, "hourly_kwh_usage" would resolve against /api/ instead of /api/CustomerRESTApi/.
    internal static void ValidateBaseAddress(HttpClient http)
    {
        if (http.BaseAddress is null || !http.BaseAddress.AbsoluteUri.EndsWith('/'))
        {
            throw new ArgumentException(
                $"HttpClient.BaseAddress must be set and end with '/', e.g. {DefaultBaseAddress}", nameof(http));
        }
    }

    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var retryAfter = response.Headers.RetryAfter;
        throw new SevApiException(
            response.StatusCode,
            body,
            retryAfter?.Delta ?? retryAfter?.Date - DateTimeOffset.UtcNow);
    }
}
