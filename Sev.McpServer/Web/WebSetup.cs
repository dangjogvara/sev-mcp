using Sev.McpServer.Web.Components;

namespace Sev.McpServer.Web;

// The web pages (Blazor, rendered on the server as plain HTML). They run in the same app as the MCP tools on
// purpose: the SEV rate-limit count, login, cache and heat pump log are in-memory singletons, and a second app
// would have its own and could together go over SEV's limit.
public static class WebSetup
{
    public static void AddSevWeb(this WebApplicationBuilder builder) =>
        builder.Services.AddRazorComponents();

    // The pages have no login of their own yet, so they're only mapped when the server runs without Auth: locally,
    // where AllowedHosts and LocalOrigin already keep everything to this machine. Hosted (Auth set) they don't exist.
    public static void MapSevWeb(this WebApplication app, AuthOptions auth)
    {
        if (auth.Enabled)
        {
            return;
        }
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorComponents<App>();
    }
}
