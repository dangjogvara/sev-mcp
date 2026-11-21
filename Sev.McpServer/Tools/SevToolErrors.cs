using System.Net;
using ModelContextProtocol;
using Sev.Client;

namespace Sev.McpServer.Tools;

// The MCP SDK only shows Claude the message of an McpException; any other exception becomes a
// generic error. Translate SEV failures into messages Claude can act on or pass to the user.
public static class SevToolErrors
{
    public static async Task<T> CallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (SevApiException ex)
        {
            throw new McpException(Describe(ex), ex);
        }
    }

    // When the rate limit is exceeded, SEV answers 200 OK with an empty list instead of an error
    // (observed 2026-11-21). An empty answer would otherwise read as "no meters" or "no usage".
    public const string EmptyResultMessage =
        "SEV returned no data. This usually means the SEV rate limit (10 calls per 5 minutes) has been reached; " +
        "SEV doesn't report that as an error. Try again in about 5 minutes.";

    public static IReadOnlyList<T> ThrowIfEmpty<T>(IReadOnlyList<T> result) =>
        result.Count > 0 ? result : throw new McpException(EmptyResultMessage);

    public static string Describe(SevApiException ex) => ex.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "SEV rejected the server's credentials. The server's SEV user name or API key needs to be checked.",
        HttpStatusCode.TooManyRequests =>
            "SEV rate limit reached (10 calls per 5 minutes)."
            + (ex.RetryAfter is { } wait ? $" Try again in {Math.Ceiling(wait.TotalSeconds)} seconds." : " Try again in a few minutes."),
        _ => ex.Message,
    };
}
