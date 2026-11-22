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

    public Task<IReadOnlyList<MeterUsage>> GetHourlyUsageAsync(
        IReadOnlyList<int> meterIds, DateTime from, DateTime to, CancellationToken cancellationToken = default) =>
        PostHourlyAsync<MeterUsage>("hourly_kwh_usage", meterIds, from, to, cancellationToken);

    // SEV's estimate of what the electricity used cost, per hour, in DKK.
    public Task<IReadOnlyList<MeterCost>> GetEstimatedCostAsync(
        IReadOnlyList<int> meterIds, DateTime from, DateTime to, CancellationToken cancellationToken = default) =>
        PostHourlyAsync<MeterCost>("estimated_cost", meterIds, from, to, cancellationToken);

    // SEV's estimate of the CO2 emitted for the electricity used, per hour, in kg.
    // Per SEV's documentation: (1 - the hour's green share of production) * kWh * 0.641 kg CO2/kWh.
    public Task<IReadOnlyList<MeterCo2>> GetEstimatedCo2Async(
        IReadOnlyList<int> meterIds, DateTime from, DateTime to, CancellationToken cancellationToken = default) =>
        PostHourlyAsync<MeterCo2>("estimated_CO2", meterIds, from, to, cancellationToken);

    // All hourly endpoints take the same request and return one entry per meter; only the reading type differs.
    private async Task<IReadOnlyList<TMeter>> PostHourlyAsync<TMeter>(
        string endpoint, IReadOnlyList<int> meterIds, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        ValidateRange(from, to);
        var request = new HourlyRequest(meterIds, from, to);

        using var response = await _http.PostAsJsonAsync(endpoint, request, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<List<TMeter>>(JsonOptions, cancellationToken) ?? [];
    }

    // The API works in local Faroese time and serializes DateTime as-is, so a Utc or Local
    // DateTime would be sent with "Z" or an offset and shift the requested hours.
    internal static void ValidateRange(DateTime from, DateTime to)
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
