using System.Net;
using System.Text.Json;
using ModelContextProtocol;
using MyUplink.Client;

namespace Sev.McpServer.Tools;

// The heat pump tools' counterpart to SevToolErrors: only an McpException's message reaches Claude, so myUplink
// failures are turned into messages it can act on or pass to the user.
public static class HeatPumpToolErrors
{
    public static async Task<T> CallAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken = default)
    {
        try
        {
            return await call();
        }
        catch (MyUplinkApiException ex)
        {
            throw new McpException(Describe(ex), ex);
        }
        catch (HttpRequestException ex)
        {
            throw new McpException($"Could not reach myUplink: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new McpException("myUplink timed out. Try again in a moment.", ex);
        }
        catch (JsonException ex)
        {
            throw new McpException("myUplink returned an unexpected response that could not be read.", ex);
        }
    }

    // A 400 means wrong credentials only at the login; elsewhere it's an ordinary error, shown with myUplink's answer.
    public static string Describe(MyUplinkApiException ex) => ex.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "myUplink rejected the server's application credentials (MyUplink client ID or secret), or refused access. They need to be checked.",
        HttpStatusCode.BadRequest when ex.AtLogin =>
            "myUplink rejected the server's application credentials (MyUplink client ID or secret), or refused access. They need to be checked.",
        HttpStatusCode.TooManyRequests =>
            "myUplink allows 25 calls per minute and they are used up."
            + (ex.RetryAfter is { } wait && wait > TimeSpan.Zero ? $" Try again in {Math.Ceiling(wait.TotalSeconds)} seconds." : " Try again in a minute."),
        _ => ex.Message,
    };
}
