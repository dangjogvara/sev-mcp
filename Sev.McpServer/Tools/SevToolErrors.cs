using System.Net;
using System.Text.Json;
using ModelContextProtocol;
using Sev.Client;

namespace Sev.McpServer.Tools;

// The MCP SDK only shows Claude the message of an McpException; any other exception becomes a
// generic error. Translate SEV failures into messages Claude can act on or pass to the user.
public static class SevToolErrors
{
    public static async Task<T> CallAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken = default)
    {
        try
        {
            return await call();
        }
        catch (SevApiException ex)
        {
            throw new McpException(Describe(ex), ex);
        }
        catch (SevRateLimitExceededException ex)
        {
            throw new McpException(
                $"The SEV API allows only 10 calls per 5 minutes and they are used up. Try again in {Math.Ceiling(ex.RetryAfter.TotalSeconds)} seconds.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new McpException($"Could not reach the SEV API: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new McpException("The SEV API timed out. Try again in a moment.", ex);
        }
        catch (JsonException ex)
        {
            throw new McpException("The SEV API returned an unexpected response that could not be read.", ex);
        }
    }

    // When the rate limit is exceeded, SEV answers 200 OK with an empty list instead of an error
    // (observed 2026-11-21). An empty answer would otherwise read as "no meters" or "no usage".
    public const string EmptyResultMessage =
        "SEV returned no data. This usually means the SEV rate limit (10 calls per 5 minutes) has been reached; " +
        "SEV doesn't report that as an error. Try again in about 5 minutes.";

    public static IReadOnlyList<T> ThrowIfEmpty<T>(IReadOnlyList<T> result) =>
        result.Count > 0 ? result : throw new McpException(EmptyResultMessage);

    // For usage, cost and CO2, only SEV's own empty answer counts: a range with nothing to fetch yet (the future)
    // isn't one, and when cached days can still be shown, the rest is reported as not fetched instead.
    public static T ThrowIfEmptyAnswer<T>(T result) where T : IHourlyResult =>
        result.EmptyAnswer && result.Meters.All(m => m.Readings.Count == 0)
            ? throw new McpException(EmptyResultMessage)
            : result;

    public static string Describe(SevApiException ex) => ex.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "SEV rejected the server's credentials. The server's SEV user name or API key needs to be checked.",
        HttpStatusCode.TooManyRequests =>
            "SEV rate limit reached (10 calls per 5 minutes)."
            + (ex.RetryAfter is { } wait && wait > TimeSpan.Zero ? $" Try again in {Math.Ceiling(wait.TotalSeconds)} seconds." : " Try again in a few minutes."),
        _ => ex.Message,
    };
}
