using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using MyUplink.Client;
using MyUplink.Client.Models;
using Sev.McpServer.HeatPump;
using Sev.McpServer.Tools;

namespace Sev.McpServer.Tests;

public class HeatPumpToolsTests
{
    private static readonly MyUplinkDevice HeatPump = new("DEV1", "Connected", "20230301", new MyUplinkProduct("000", "CTC EcoZenith i360"));

    private static readonly MyUplinkSystem System = new("SYS1", "Test heat pump", false, [HeatPump]);

    private static DataPoint Point(
        string id, string? name, string? category, double? value, string? unit = null, bool writable = false,
        string? strVal = null, IReadOnlyList<EnumValue>? labels = null) =>
        new(category, id, name, unit, writable, DateTimeOffset.UnixEpoch, value, strVal, labels);

    [Fact]
    public void Summarize_GroupsByCategoryInOrderAndSkipsUnnamedAndUnconnected()
    {
        var points = new[]
        {
            Point("62000", "Outdoor Temperature", "General", 3.5, "°C"),
            Point("62003", "Hot water flow temp", "Hot water", HeatPumpReadings.NotConnected, "°C"),
            Point("62276", "Hot water tank temp", "Hot water", 52, "°C"),
            Point("62002", "Text not found: id[], fw[CTC-01-01], lang[en-GB]", "Text not found: id[], fw[CTC-01-01], lang[en-GB]", 0),
            Point("62193", "RPS", "Heat pump 1", 42),
            Point("62214", "Operation time HP1", "Text not found: id[], fw[CTC-01-01], lang[en-GB]", 18000, "h"),
        };

        var result = HeatPumpTools.Summarize(System, Points(points), []);

        var device = Assert.Single(result.Devices);
        Assert.Equal(["General", "Hot water", "Heat pump 1", "Other"], device.Categories.Select(c => c.Category));
        Assert.Equal(["Operation time HP1"], device.Categories[3].Values.Select(v => v.Name));
        Assert.Equal(["Hot water tank temp"], device.Categories[1].Values.Select(v => v.Name));
        Assert.Equal("CTC EcoZenith i360", device.Product);
        Assert.Null(device.Note);
    }

    [Fact]
    public void Summarize_GivesLabelsForCodedValuesAndUnitsForMeasurements()
    {
        var points = new[]
        {
            Point("62005", "Status", "General", 4, strVal: "HC", labels: [new EnumValue("4", "HC"), new EnumValue("7", "Off")]),
            Point("62000", "Outdoor Temperature", "General", 3.5, "°C", strVal: "3.5°C"),
            Point("62193", "RPS", "Heat pump 1", 42, unit: ""),
            Point("61502", "Hot water setpoint", "Hot water", 50, "°C", writable: true),
        };

        var values = HeatPumpTools.Summarize(System, Points(points), []).Devices[0].Categories
            .SelectMany(c => c.Values)
            .ToDictionary(v => v.Name);

        Assert.Equal(new HeatPumpValue("Status", null, "HC", null, null), values["Status"]);
        Assert.Equal(new HeatPumpValue("Outdoor Temperature", 3.5, null, "°C", null), values["Outdoor Temperature"]);
        Assert.Equal(new HeatPumpValue("RPS", 42, null, null, null), values["RPS"]);
        Assert.Equal(new HeatPumpValue("Hot water setpoint", 50, null, "°C", true), values["Hot water setpoint"]);
    }

    [Fact]
    public void Summarize_OfflineDeviceGetsANote()
    {
        var system = System with { Devices = [HeatPump with { ConnectionState = "Disconnected" }] };

        var device = Assert.Single(HeatPumpTools.Summarize(system, Points([]), []).Devices);

        Assert.Contains("isn't connected", device.Note);
    }

    [Fact]
    public void Summarize_ListsAlarmsInFaroeseTime()
    {
        var system = System with { HasAlarm = true };
        // Summer time: Faroese time is UTC+1.
        var alarm = new MyUplinkAlarm(15, "DEV1", 1, "2026-07-01T08:15:00Z", "Low flow", "Check the heating circuit.", "Heat pump 1");

        var result = HeatPumpTools.Summarize(system, Points([]), [alarm]);

        Assert.True(result.HasAlarm);
        Assert.Equal(new HeatPumpAlarm("Low flow", "Check the heating circuit.", 1, "2026-07-01T09:15", "Heat pump 1"), Assert.Single(result.Alarms));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "credentials")]
    [InlineData(HttpStatusCode.Forbidden, "credentials")]
    [InlineData(HttpStatusCode.TooManyRequests, "30 seconds")]
    public void Describe_ExplainsFailures(HttpStatusCode status, string expected)
    {
        var message = HeatPumpToolErrors.Describe(new MyUplinkApiException(status, "", TimeSpan.FromSeconds(29.5)));

        Assert.Contains(expected, message);
    }

    // --- Registration: the tool only exists when myUplink is configured ---

    [Fact]
    public void WithoutMyUplink_ThereIsNoHeatPumpTool()
    {
        using var factory = Server(clientId: "", clientSecret: "");

        var names = ToolNames(factory);

        Assert.Contains("get_usage", names);
        Assert.DoesNotContain("get_heatpump_status", names);
        Assert.DoesNotContain("get_heatpump_history", names);
    }

    [Fact]
    public void LogHourlyOff_NoBackgroundLogger()
    {
        using var factory = Server(clientId: "client-id", clientSecret: "client-secret");

        Assert.DoesNotContain(factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), s => s is HeatPumpLogger);
    }

    [Fact]
    public void WithMyUplink_TheHeatPumpToolIsRegistered()
    {
        using var factory = Server(clientId: "client-id", clientSecret: "client-secret");

        var names = ToolNames(factory);

        Assert.Contains("get_usage", names);
        Assert.Contains("get_heatpump_status", names);
        Assert.Contains("get_heatpump_history", names);
    }

    [Fact]
    public void HalfConfiguredMyUplink_StopsTheServer()
    {
        using var factory = Server(clientId: "client-id", clientSecret: "");

        var ex = Assert.ThrowsAny<Exception>(() => ToolNames(factory));

        Assert.Contains("MyUplink needs both", ex.ToString());
    }

    private static Dictionary<string, IReadOnlyList<DataPoint>> Points(IReadOnlyList<DataPoint> points) =>
        new() { ["DEV1"] = points };

    private static List<string> ToolNames(WebApplicationFactory<Program> factory) =>
        factory.Services.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name).ToList();

    // Empty values override anything in the developer's user-secrets, so "not configured" really is.
    private static WebApplicationFactory<Program> Server(string clientId, string clientSecret) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Sev:User", "test-user");
            builder.UseSetting("Sev:Key", "test-key");
            builder.UseSetting("MyUplink:ClientId", clientId);
            builder.UseSetting("MyUplink:ClientSecret", clientSecret);
            // The hourly logger would call myUplink as soon as the server starts.
            builder.UseSetting("MyUplink:LogHourly", "false");
        });
}
