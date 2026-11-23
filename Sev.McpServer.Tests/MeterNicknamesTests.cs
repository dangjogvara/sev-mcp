using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sev.McpServer.Tools;

namespace Sev.McpServer.Tests;

public class MeterNicknamesTests
{
    private static readonly MeterSummary Kitchen = new(1111, "E350", "E-01", "Kjallari", "Testgøta 1, 100 Tórshavn", null, null, false);
    private static readonly MeterSummary Heat = new(2222, "E360 LTE", "E-02", null, "Testgøta 1, 100 Tórshavn", null, null, false);

    [Fact]
    public void Apply_ReplacesOnlyConfiguredMeters()
    {
        var nicknames = MeterNicknames.From(new Dictionary<string, string> { ["2222"] = " Hitapumpa " });

        var meters = nicknames.Apply([Kitchen, Heat]);

        Assert.Equal(["Kjallari", "Hitapumpa"], meters.Select(m => m.Nickname));
    }

    [Fact]
    public void ConfiguredNickname_ReplacesSevs()
    {
        var nicknames = MeterNicknames.From(new Dictionary<string, string> { ["1111"] = "House" });

        Assert.Equal("House", Assert.Single(nicknames.Apply([Kitchen])).Nickname);
    }

    [Fact]
    public void ConfiguredNickname_PicksTheMeterInTheUsageTools()
    {
        var meters = MeterNicknames.From(new Dictionary<string, string> { ["2222"] = "Hitapumpa" }).Apply([Kitchen, Heat]);

        Assert.Equal(2222, Assert.Single(UsageTools.ResolveMeters(meters, ["hitapumpa"])).MeterId);
    }

    [Fact]
    public void None_LeavesTheListAsItIs()
    {
        IReadOnlyList<MeterSummary> meters = [Kitchen, Heat];

        Assert.Same(meters, MeterNicknames.None.Apply(meters));
    }

    [Theory]
    [InlineData("abc", "House", "isn't a meter ID")]
    [InlineData("-1", "House", "isn't a meter ID")]
    [InlineData("1111", " ", "is empty")]
    public void Problems_NameTheBadSetting(string key, string value, string expected)
    {
        var problem = Assert.Single(MeterNicknames.Problems(new Dictionary<string, string> { [key] = value }));

        Assert.Contains(expected, problem);
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
