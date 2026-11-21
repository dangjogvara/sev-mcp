using ModelContextProtocol;
using Sev.Client.Models;
using Sev.McpServer.Tools;

namespace Sev.McpServer.Tests;

public class UsageToolsTests
{
    [Fact]
    public void ParseRange_ConvertsBothEnds()
    {
        var (from, to) = UsageTools.ParseRange([1111], "2026-07-14", "2026-07-15T00:00:00+01:00");

        Assert.Equal(new DateTime(2026, 7, 14), from);
        Assert.Equal(new DateTime(2026, 7, 15), to);
        Assert.Equal(DateTimeKind.Unspecified, from.Kind);
        Assert.Equal(DateTimeKind.Unspecified, to.Kind);
    }

    [Fact]
    public void ParseRange_AllowsExactlyMaxRange()
    {
        var (from, to) = UsageTools.ParseRange([1111], "2026-10-01", "2026-11-01");

        Assert.Equal(TimeSpan.FromDays(31), to - from);
    }

    [Theory]
    [InlineData(new int[0], "2026-10-10", "2026-10-11", "meter")]
    [InlineData(new[] { 1111 }, "yesterday", "2026-10-11", "'from'")]
    [InlineData(new[] { 1111 }, "2026-10-10", "nope", "'to'")]
    [InlineData(new[] { 1111 }, "2026-10-10", "2026-10-10", "before")]
    [InlineData(new[] { 1111 }, "2026-10-11", "2026-10-10", "before")]
    [InlineData(new[] { 1111 }, "2026-10-01", "2026-11-01T01:00", "31 days")]
    public void ParseRange_RejectsInvalidArgumentsWithHelpfulMessage(int[] meterIds, string from, string to, string expectedInMessage)
    {
        var ex = Assert.Throws<McpException>(() => UsageTools.ParseRange(meterIds, from, to));

        Assert.Contains(expectedInMessage, ex.Message);
    }

    [Fact]
    public void Summarize_RoundsValuesAndAddsTotal()
    {
        var usage = new List<MeterUsage>
        {
            new("2222",
            [
                new(new DateTime(2026, 10, 10, 0, 0, 0), 1.167m, 18184.133m, "kWh"),
                new(new DateTime(2026, 10, 10, 1, 0, 0), 0.7539999999971769m, 18184.887m, "kWh"),
            ]),
        };

        var meter = Assert.Single(UsageTools.Summarize(usage));

        Assert.Equal("2222", meter.MeterId);
        Assert.Equal("kWh", meter.Unit);
        Assert.Equal(1.921m, meter.Total);
        Assert.Equal(
            [new HourlyReading("2026-10-10T00:00", 1.167m), new HourlyReading("2026-10-10T01:00", 0.754m)],
            meter.Readings);
    }

    [Fact]
    public void Summarize_MeterWithoutReadings()
    {
        var meter = Assert.Single(UsageTools.Summarize([new MeterUsage("2222", [])]));

        Assert.Equal(0m, meter.Total);
        Assert.Empty(meter.Readings);
    }
}
