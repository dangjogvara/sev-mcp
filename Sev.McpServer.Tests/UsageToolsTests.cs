using ModelContextProtocol;
using Sev.Core;
using Sev.McpServer.Tools;

namespace Sev.McpServer.Tests;

public class UsageToolsTests
{
    private static readonly MeterSummary Kitchen = Meter(1111, "E350", nickname: "Køkur", address: "Testgøta 1, 100 Tórshavn");
    private static readonly MeterSummary Heat = Meter(2222, "E360 LTE", nickname: null, address: "Testgøta 1, 100 Tórshavn");
    private static readonly MeterSummary Cabin = Meter(3333, "E350", nickname: "Hytta", address: "Bygdavegur 5, 410 Kollafjørður");
    private static readonly MeterSummary Old = Meter(4444, "E200", nickname: null, address: "Testgøta 1, 100 Tórshavn", removed: true);
    private static readonly MeterSummary[] All = [Kitchen, Heat, Cabin, Old];

    // --- Arguments (the checks themselves are tested in Sev.Core.Tests.PeriodsTests) ---

    [Fact]
    public void ParsePeriod_PassesCoreErrorsOnAsMcpException()
    {
        var ex = Assert.Throws<McpException>(() => ToolInput.ParsePeriod("2026-10-10", "2026-10-10", "day"));

        Assert.Contains("before", ex.Message);
        Assert.IsType<InvalidInputException>(ex.InnerException);
    }

    [Fact]
    public void ParsePeriod_ReadsAllThree()
    {
        var (granularity, from, to) = ToolInput.ParsePeriod("2026-10-10", "2026-10-12", "Month");

        Assert.Equal(Granularity.Month, granularity);
        Assert.Equal(new DateTime(2026, 10, 10), from);
        Assert.Equal(new DateTime(2026, 10, 12), to);
    }

    // --- Meter selection ---

    [Fact]
    public void ResolveMeters_NoneGiven_ReturnsActiveMeters()
    {
        Assert.Equal([Kitchen, Heat, Cabin], UsageTools.ResolveMeters(All, null));
        Assert.Equal([Kitchen, Heat, Cabin], UsageTools.ResolveMeters(All, []));
    }

    [Fact]
    public void ResolveMeters_NoActiveMeters_ReturnsAll()
    {
        Assert.Equal([Old], UsageTools.ResolveMeters([Old], null));
    }

    [Theory]
    [InlineData("2222", 2222)]
    [InlineData("e360 lte", 2222)]
    [InlineData("køkur", 1111)]
    [InlineData("hyt", 3333)]
    [InlineData("Kollafjørður", 3333)]
    [InlineData("4444", 4444)]
    public void ResolveMeters_ByIdNameNicknameOrAddress(string item, int expectedId)
    {
        var meter = Assert.Single(UsageTools.ResolveMeters(All, [item]));

        Assert.Equal(expectedId, meter.MeterId);
    }

    [Fact]
    public void ResolveMeters_SharedAddressOrName_ReturnsAllMatchesOnce()
    {
        Assert.Equal([Kitchen, Heat, Old], UsageTools.ResolveMeters(All, ["Testgøta 1"]));
        Assert.Equal([Kitchen, Cabin], UsageTools.ResolveMeters(All, ["E350", "1111"]));
    }

    [Fact]
    public void ResolveMeters_Unknown_ListsAvailableMeters()
    {
        var ex = Assert.Throws<McpException>(() => UsageTools.ResolveMeters(All, ["garage"]));

        Assert.Contains("No meter matches 'garage'", ex.Message);
        Assert.Contains("1111 E350 \"Køkur\" at Testgøta 1, 100 Tórshavn", ex.Message);
        Assert.Contains("2222 E360 LTE at Testgøta 1", ex.Message);
    }

    private static MeterSummary Meter(int id, string name, string? nickname, string address, bool removed = false) =>
        new(id, name, "E-01", nickname, address, new DateOnly(2013, 3, 12), removed ? new DateOnly(2024, 1, 1) : null, removed);
}
