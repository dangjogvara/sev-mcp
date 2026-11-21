using System.Net.Http.Headers;

namespace Sev.Client;

// Adds "Authorization: Bearer <token>" to every request sent through the HttpClient it is part of.
// Setting the header per request (not on HttpClient.DefaultRequestHeaders) is safe with
// IHttpClientFactory and with concurrent requests.
public class SevAuthHandler(SevTokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokens.GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}
