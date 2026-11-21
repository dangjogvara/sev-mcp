using System.Net;

namespace Sev.Client;

// Thrown when the SEV API answers with a non-success status code. Keeps the status,
// response body and Retry-After so callers can tell 401 / 429 / 400 apart.
public class SevApiException(HttpStatusCode statusCode, string responseBody, TimeSpan? retryAfter)
    : HttpRequestException(BuildMessage(statusCode, responseBody), null, statusCode)
{
    public string ResponseBody { get; } = responseBody;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    private static string BuildMessage(HttpStatusCode statusCode, string responseBody)
    {
        var message = $"SEV API returned {(int)statusCode} ({statusCode}).";
        return string.IsNullOrWhiteSpace(responseBody) ? message : $"{message} {Truncate(responseBody, 500)}";
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";
}
