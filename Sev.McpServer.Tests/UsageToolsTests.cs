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

    // "Now" for the Summarize tests: well after the periods they ask about, unless a test says otherwise.
    private static readonly DateTime Later = new(2026, 11, 21, 12, 0, 0);

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

        var meter = Assert.Single(UsageTools.Summarize(
            [2222], usage, new DateTime(2026, 10, 10, 0, 0, 0), new DateTime(2026, 10, 10, 2, 0, 0), Later));

        Assert.Equal("2222", meter.MeterId);
        Assert.Equal("kWh", meter.Unit);
        Assert.Equal(1.921m, meter.Total);
        Assert.Equal(2, meter.ExpectedHours);
        Assert.Equal(0, meter.MissingHours);
        Assert.Null(meter.Note);
        Assert.Equal(
            [new HourlyReading("2026-10-10T00:00", 1.167m), new HourlyReading("2026-10-10T01:00", 0.754m)],
            meter.Readings);
    }

    [Fact]
    public void Summarize_MeterWithoutReadings_HasNoTotalAndSaysDataIsMissing()
    {
        var meter = Assert.Single(UsageTools.Summarize(
            [2222], [new MeterUsage("2222", [])], Day(2026, 10, 10), Day(2026, 10, 11), Later));

        Assert.Null(meter.Total);
        Assert.Equal(24, meter.ExpectedHours);
        Assert.Equal(24, meter.MissingHours);
        Assert.Contains("not zero", meter.Note);
        Assert.Empty(meter.Readings);
    }

    [Fact]
    public void Summarize_MeterLeftOutBySev_IsReportedAsMissing()
    {
        var results = UsageTools.Summarize(
            [1111, 2222], [FullDay("2222", 2026, 10, 10)], Day(2026, 10, 10), Day(2026, 10, 11), Later);

        Assert.Equal(["1111", "2222"], results.Select(r => r.MeterId));
        Assert.Null(results[0].Total);
        Assert.Contains("not zero", results[0].Note);
        Assert.Equal(24m, results[1].Total);
    }

    [Fact]
    public void Summarize_SomeHoursMissing_SaysTotalIsTooLow()
    {
        var readings = FullDay("2222", 2026, 10, 10).Readings.Take(20).ToList();

        var meter = Assert.Single(UsageTools.Summarize(
            [2222], [new MeterUsage("2222", readings)], Day(2026, 10, 10), Day(2026, 10, 11), Later));

        Assert.Equal(20m, meter.Total);
        Assert.Equal(4, meter.MissingHours);
        Assert.Contains("4 of 24 hours", meter.Note);
        Assert.Contains("too low", meter.Note);
    }

    [Fact]
    public void Summarize_Today_OnlyHoursThatHaveEndedAreExpected()
    {
        var now = new DateTime(2026, 11, 21, 10, 30, 0);
        var readings = Enumerable.Range(0, 10)
            .Select(h => new UsageReading(Day(2026, 11, 21).AddHours(h), 1m, null, "kWh"))
            .ToList();

        var meter = Assert.Single(UsageTools.Summarize(
            [2222], [new MeterUsage("2222", readings)], Day(2026, 11, 21), Day(2026, 11, 22), now));

        Assert.Equal(10, meter.ExpectedHours);
        Assert.Equal(0, meter.MissingHours);
        Assert.Null(meter.Note);
    }

    [Fact]
    public void Summarize_FuturePeriod_SaysItHasNotHappened()
    {
        var meter = Assert.Single(UsageTools.Summarize(
            [2222], [new MeterUsage("2222", [])], Day(2026, 12, 1), Day(2026, 12, 2), Later));

        Assert.Null(meter.Total);
        Assert.Equal(0, meter.ExpectedHours);
        Assert.Contains("hasn't happened", meter.Note);
    }

    [Theory]
    // Summer time starts 2026-03-29 (01:00 → 02:00): 23 local hours.
    [InlineData(2026, 3, 29, 23)]
    // Summer time ends 2026-10-25 (02:00 → 01:00): the repeated hour has the same local label, so 24 labels.
    [InlineData(2026, 10, 25, 24)]
    [InlineData(2026, 10, 10, 24)]
    public void ExpectedHours_FollowFaroeseClock(int year, int month, int day, int expected)
    {
        var hours = UsageTools.ExpectedHours(Day(year, month, day), Day(year, month, day).AddDays(1), Later);

        Assert.Equal(expected, hours.Count);
        Assert.Equal(expected, hours.Distinct().Count());
    }

    [Fact]
    public void ExpectedHours_PartialHourAtStartIsNotExpected()
    {
        var hours = UsageTools.ExpectedHours(
            new DateTime(2026, 10, 10, 6, 30, 0), new DateTime(2026, 10, 10, 9, 0, 0), Later);

        Assert.Equal([new DateTime(2026, 10, 10, 7, 0, 0), new DateTime(2026, 10, 10, 8, 0, 0)], hours);
    }

    private static DateTime Day(int year, int month, int day) => new(year, month, day);

    private static MeterUsage FullDay(string meterId, int year, int month, int day) =>
        new(meterId, Enumerable.Range(0, 24)
            .Select(h => new UsageReading(Day(year, month, day).AddHours(h), 1m, null, "kWh"))
            .ToList());
}
