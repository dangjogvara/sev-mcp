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
