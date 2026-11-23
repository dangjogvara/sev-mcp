using Sev.Core;

namespace Sev.Core.Tests;

// Reading and checking from / to / granularity, and the hours that should have a reading.
public class PeriodsTests
{
    // "Now" for ExpectedHours: well after the periods it asks about.
    private static readonly DateTime Later = new(2026, 11, 21, 12, 0, 0);

    [Theory]
    [InlineData("hour", Granularity.Hour)]
    [InlineData("Day", Granularity.Day)]
    [InlineData("MONTH", Granularity.Month)]
    public void ParseGranularity_IgnoresCase(string input, Granularity expected)
    {
        Assert.Equal(expected, Periods.ParseGranularity(input));
    }

    [Theory]
    [InlineData("week")]
    [InlineData("")]
    [InlineData("1")]
    public void ParseGranularity_RejectsUnknown(string input)
    {
        var ex = Assert.Throws<InvalidInputException>(() => Periods.ParseGranularity(input));

        Assert.Contains("'hour', 'day' or 'month'", ex.Message);
    }

    [Fact]
    public void ParseRange_ConvertsBothEnds()
    {
        var (from, to) = Periods.ParseRange("2026-07-14", "2026-07-15T00:00:00+01:00", Granularity.Day);

        Assert.Equal(new DateTime(2026, 7, 14), from);
        Assert.Equal(new DateTime(2026, 7, 15), to);
        Assert.Equal(DateTimeKind.Unspecified, from.Kind);
        Assert.Equal(DateTimeKind.Unspecified, to.Kind);
    }

    [Theory]
    [InlineData(Granularity.Hour, "2026-10-01", "2026-11-01")]
    [InlineData(Granularity.Day, "2025-11-21", "2026-11-22")]
    [InlineData(Granularity.Month, "2024-11-21", "2026-11-22")]
    public void ParseRange_AllowsExactlyMaxRange(Granularity granularity, string from, string to)
    {
        var (fromTime, toTime) = Periods.ParseRange(from, to, granularity);

        Assert.Equal(Periods.MaxRange(granularity), toTime - fromTime);
    }

    [Theory]
    [InlineData("yesterday", "2026-10-11", Granularity.Day, "'from'")]
    [InlineData("2026-10-10", "nope", Granularity.Day, "'to'")]
    [InlineData("2026-10-10", "2026-10-10", Granularity.Day, "before")]
    [InlineData("2026-10-11", "2026-10-10", Granularity.Day, "before")]
    [InlineData("2026-10-01", "2026-11-01T01:00", Granularity.Hour, "31 days")]
    [InlineData("2025-11-21", "2026-11-22T01:00", Granularity.Day, "366 days")]
    [InlineData("2024-11-21", "2026-11-22T01:00", Granularity.Month, "731 days")]
    public void ParseRange_RejectsInvalidArgumentsWithHelpfulMessage(string from, string to, Granularity granularity, string expectedInMessage)
    {
        var ex = Assert.Throws<InvalidInputException>(() => Periods.ParseRange(from, to, granularity));

        Assert.Contains(expectedInMessage, ex.Message);
    }

    // --- Expected hours ---

    [Theory]
    // Summer time starts 2026-03-29 (01:00 → 02:00): 23 local hours.
    [InlineData(2026, 3, 29, 23)]
    // Summer time ends 2026-10-25 (02:00 → 01:00): the repeated hour has the same local label, so 24 labels.
    [InlineData(2026, 10, 25, 24)]
    [InlineData(2026, 10, 10, 24)]
    public void ExpectedHours_FollowFaroeseClock(int year, int month, int day, int expected)
    {
        var hours = Periods.ExpectedHours(Day(year, month, day), Day(year, month, day).AddDays(1), Later);

        Assert.Equal(expected, hours.Count);
        Assert.Equal(expected, hours.Distinct().Count());
    }

    [Fact]
    public void ExpectedHours_PartialHourAtStartIsNotExpected()
    {
        var hours = Periods.ExpectedHours(
            new DateTime(2026, 10, 10, 6, 30, 0), new DateTime(2026, 10, 10, 9, 0, 0), Later);

        Assert.Equal([new DateTime(2026, 10, 10, 7, 0, 0), new DateTime(2026, 10, 10, 8, 0, 0)], hours);
    }

    private static DateTime Day(int year, int month, int day) => new(year, month, day);
}
