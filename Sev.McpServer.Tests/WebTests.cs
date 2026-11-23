using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Sev.McpServer.Tests;

// The web pages have no login yet, so they must exist locally and not at all when the server is hosted.
public class WebTests
{
    [Fact]
    public async Task UsagePage_Locally_IsShown()
    {
        using var factory = Server(withAuth: false);

        var response = await factory.CreateClient().GetAsync("/usage");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<h1>Usage</h1>", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Stylesheet_Locally_IsServed()
    {
        using var factory = Server(withAuth: false);

        var response = await factory.CreateClient().GetAsync("/app.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/usage")]
    [InlineData("/app.css")]
    public async Task Pages_Hosted_DoNotExist(string path)
    {
        using var factory = Server(withAuth: true);

        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static WebApplicationFactory<Program> Server(bool withAuth) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(withAuth ? "Production" : "Development");
            builder.UseSetting("Sev:User", "test-user");
            builder.UseSetting("Sev:Key", "test-key");
            if (withAuth)
            {
                builder.UseSetting("Auth:Authority", "https://login.example.com/tenant-id/v2.0");
                builder.UseSetting("Auth:Resource", "https://sev.example.com/mcp");
                builder.UseSetting("Auth:Audiences:0", "api-client-id");
                builder.UseSetting("Auth:Scopes:0", "api://api-client-id/mcp.access");
                builder.UseSetting("Auth:AllowedUsers:0", "11111111-1111-1111-1111-111111111111");
            }
        });
}
