using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Sev.Client.Models;

namespace Sev.Client;

public class SevClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task LoginAsync(string userName, string apiKey)
    {
        var response = await http.PostAsJsonAsync(
            "login_and_get_jwt_token",
            new { UserName = userName, Password = apiKey },
            Json
        );
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadAsStringAsync();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<List<Customer>> GetAvailableMetersAsync()
    {
        var response = await http.PostAsync("get_available_meters", null);
        response.EnsureSuccessStatusCode();

        var customers = await response.Content.ReadFromJsonAsync<List<Customer>>(Json);
        return customers ?? [];

    }
}



