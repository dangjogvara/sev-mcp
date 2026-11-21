namespace Sev.McpServer;

// The MCP spec asks Streamable HTTP servers to validate the Origin header, so a web page can't use
// DNS rebinding to call a local server from the user's browser. ModelContextProtocol.AspNetCore doesn't.
// Non-browser clients such as Claude Code send no Origin and are allowed; browsers are allowed only from
// a localhost page (e.g. the MCP Inspector). Hosting will need authentication and its own list.
public static class LocalOrigin
{
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
