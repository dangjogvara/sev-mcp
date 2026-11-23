using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sev.Core;
using Sev.McpServer.Tools;

namespace Sev.McpServer.Tests;

// How the server uses meter nicknames; MeterNicknames itself is tested in Sev.Core.Tests.
public class MeterNicknamesTests
{
    private static readonly MeterSummary Kitchen = new(1111, "E350", "E-01", "Kjallari", "Testgøta 1, 100 Tórshavn", null, null, false);
    private static readonly MeterSummary Heat = new(2222, "E360 LTE", "E-02", null, "Testgøta 1, 100 Tórshavn", null, null, false);

    [Fact]
    public void ConfiguredNickname_PicksTheMeterInTheUsageTools()
    {
        var meters = MeterNicknames.From(new Dictionary<string, string> { ["2222"] = "Hitapumpa" }).Apply([Kitchen, Heat]);

        Assert.Equal(2222, Assert.Single(UsageTools.ResolveMeters(meters, ["hitapumpa"])).MeterId);
    }

    [Fact]
    public void Server_UsesConfiguredNicknames()
    {
        using var factory = Server("2222", "Hitapumpa");

        var nicknames = factory.Services.GetRequiredService<MeterNicknames>();

        Assert.Equal("Hitapumpa", nicknames.Apply([Heat])[0].Nickname);
    }

    [Fact]
    public void Server_RefusesToStartWithABadNickname()
    {
        using var factory = Server("Hitapumpa", "2222");

        var ex = Assert.ThrowsAny<Exception>(() => factory.Services.GetRequiredService<MeterNicknames>());

        Assert.Contains("Sev:MeterNicknames", ex.ToString());
    }

    private static WebApplicationFactory<Program> Server(string key, string nickname) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Sev:User", "test-user");
            builder.UseSetting("Sev:Key", "test-key");
            builder.UseSetting($"Sev:MeterNicknames:{key}", nickname);
            builder.UseSetting("MyUplink:ClientId", "");
            builder.UseSetting("MyUplink:ClientSecret", "");
        });
}
