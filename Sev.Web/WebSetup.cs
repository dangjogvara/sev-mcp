using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Sev.Web.Components;

namespace Sev.Web;

// The web pages (Blazor, rendered on the server as plain HTML). The host (Sev.McpServer) runs them in the same app
// as the MCP tools on purpose: the SEV rate-limit count, login, cache and heat pump log are in-memory singletons,
// and a second app would have its own and could together go over SEV's limit. The host also decides when the pages
// are mapped at all (see its Program.cs).
public static class WebSetup
{
    public static void AddSevWeb(this IServiceCollection services) =>
        services.AddRazorComponents();

    public static void MapSevWeb(this WebApplication app)
    {
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorComponents<App>();
    }
}
