using Sev.Client.Models;
using Sev.McpServer.Tools;

namespace Sev.McpServer.Tests;

public class MeterToolsTests
{
    [Fact]
    public void Summarize_FlattensCustomersInstallationsAndMeters()
    {
        var customers = new List<Customer>
        {
            new("Test Customer",
            [
                Installation("Testgøta 1 ", floor: null,
                    Meter(1111, removalDate: null),
                    Meter(2222, removalDate: new DateOnly(2024, 9, 9))),
                Installation("Annargøta 2", floor: "2. hædd",
                    Meter(3333, removalDate: null)),
            ]),
        };

        var meters = MeterTools.Summarize(customers);

        Assert.Equal([1111, 2222, 3333], meters.Select(m => m.MeterId));

        Assert.Equal("Testgøta 1, 100 Tórshavn", meters[0].Address);
        Assert.False(meters[0].Removed);

        Assert.True(meters[1].Removed);
        Assert.Equal(new DateOnly(2024, 9, 9), meters[1].RemovalDate);

        Assert.Equal("Annargøta 2, 2. hædd, 100 Tórshavn", meters[2].Address);
    }

    [Fact]
    public void Summarize_CopiesMeterFields()
    {
        var customers = new List<Customer>
        {
            new("Test Customer", [Installation("Testgøta 1", floor: null, Meter(1111, removalDate: null))]),
        };

        var meter = Assert.Single(MeterTools.Summarize(customers));

        Assert.Equal("E350", meter.MeterName);
        Assert.Equal("E-01", meter.MeterType);
        Assert.Equal("Kjallari", meter.Nickname);
        Assert.Equal(new DateOnly(2013, 3, 12), meter.InstallDate);
    }

    [Fact]
    public void Summarize_MissingAddressParts_AreLeftOut()
    {
        Installation Bare(string? address, string? postalCode, string? district) =>
            new(new DateTime(2019, 6, 1), null, 10001, null, address, null, postalCode, district, 310, "OS", [Meter(1111, null)]);
        var customers = new List<Customer>
        {
            new("Test Customer", [Bare(null, "100", "Tórshavn"), Bare("Testgøta 1", null, null), Bare(null, null, null)]),
        };

        var meters = MeterTools.Summarize(customers);

        Assert.Equal(["100 Tórshavn", "Testgøta 1", ""], meters.Select(m => m.Address));
    }

    [Fact]
    public void Summarize_NoCustomers_ReturnsEmpty()
    {
        Assert.Empty(MeterTools.Summarize([]));
    }

    private static Installation Installation(string address, string? floor, params Meter[] meters) =>
        new(new DateTime(2019, 6, 1), null, 10001, null, address, floor, "100", "Tórshavn", 310, "OS", meters);

    private static Meter Meter(int meterId, DateOnly? removalDate) =>
        new(meterId, "Kjallari", "11111111", "E350", "E-01", new DateOnly(2013, 3, 12), removalDate);
}
