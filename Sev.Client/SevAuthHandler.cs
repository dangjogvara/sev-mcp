using System.Net;
using System.Net.Http.Headers;

namespace Sev.Client;

// Adds "Authorization: Bearer <token>" to every request sent through the HttpClient it is part of.
// Setting the header per request (not on HttpClient.DefaultRequestHeaders) is safe with
// IHttpClientFactory and with concurrent requests.
// If SEV answers 401 (token expired or revoked early), it logs in again and retries the request once.
public class SevAuthHandler(SevTokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokens.GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        tokens.Invalidate(token);

        var freshToken = await tokens.GetTokenAsync(cancellationToken);

        // An HttpRequestMessage can only be sent once, so the retry is a copy.
        // Reading request.Content after base.SendAsync assumes repeatable content (such as JsonContent).
        using var retry = await CloneAsync(request, cancellationToken);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", freshToken);
        return await base.SendAsync(retry, cancellationToken);
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            foreach (var header in request.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            clone.Content = content;
        }

        return clone;
    }
}
