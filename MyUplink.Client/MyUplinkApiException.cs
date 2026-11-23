using System.Net;

namespace MyUplink.Client;

// A non-success answer from myUplink. RetryAfter comes from Retry-After or myUplink's ratelimit-reset header.
// AtLogin is true when the login (/oauth/token) failed, where a 400 means the client ID or secret is wrong.
public class MyUplinkApiException(HttpStatusCode statusCode, string responseBody, TimeSpan? retryAfter, bool atLogin = false)
    : HttpRequestException($"myUplink answered {(int)statusCode} {statusCode}: {Shorten(responseBody)}", null, statusCode)
{
    public string ResponseBody { get; } = responseBody;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    public bool AtLogin { get; } = atLogin;

    internal static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken, bool atLogin = false)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new MyUplinkApiException(response.StatusCode, body, RetryAfterOf(response), atLogin);
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } retry)
        {
            // A date may already be past: then no wait, not a negative one.
            return retry.Delta ?? (retry.Date is { } date ? (date - DateTimeOffset.UtcNow is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero) : null);
        }
        // myUplink's own header: seconds until its rate-limit window resets.
        return response.Headers.TryGetValues("ratelimit-reset", out var values)
            && int.TryParse(values.FirstOrDefault(), out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    private static string Shorten(string body) => body.Length <= 200 ? body : body[..200] + "…";
}
