namespace Sev.McpServer;

// The MCP spec asks Streamable HTTP servers to validate the Origin header, so a web page can't use
// DNS rebinding to call a local server from the user's browser. ModelContextProtocol.AspNetCore doesn't.
// Non-browser clients such as Claude Code send no Origin and are allowed; browsers are allowed only from
// a localhost page (e.g. the MCP Inspector). Hosting will need authentication and its own list.
public static class LocalOrigin
{
    // Against DNS rebinding: a web page in the user's browser must not reach the unauthenticated local server.
    // AllowedHosts (appsettings.json) rejects foreign Host headers; this rejects cross-site Origin headers.
    public static IApplicationBuilder UseLocalOriginsOnly(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!IsAllowed(context.Request.Headers.Origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        });

    public static bool IsAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        // "null" (sandboxed pages, file://) and anything unparseable is rejected.
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.IsLoopback;
    }
}
