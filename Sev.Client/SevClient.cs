using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Sev.Client.Models;

namespace Sev.Client;

public class SevClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task LoginAsync(string userName, string apiKey)
    {
        var response = await http.PostAsJsonAsync(
            "login_and_get_jwt_token",
            new { UserName = userName, Password = apiKey },
            JsonOptions
        );
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadAsStringAsync();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<List<Customer>> GetAvailableMetersAsync()
    {
        var response = await http.PostAsync("get_available_meters", null);
        response.EnsureSuccessStatusCode();

        var customers = await response.Content.ReadFromJsonAsync<List<Customer>>(JsonOptions);
        return customers ?? [];
    }

    public async Task<IReadOnlyList<MeterUsage>> GetHourlyUsageAsync(IReadOnlyList<int> meterIds, DateTime from, DateTime to)
    {
        var request = new HourlyUsageRequest(meterIds, from, to);

        var response = await http.PostAsJsonAsync("hourly_kwh_usage", request, JsonOptions);

        response.EnsureSuccessStatusCode();

        var usage = await response.Content.ReadFromJsonAsync<List<MeterUsage>>(JsonOptions);

        return usage ?? [];
    }
}
