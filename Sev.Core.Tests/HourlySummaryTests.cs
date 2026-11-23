using Sev.Client.Models;
using Sev.Core;

namespace Sev.Core.Tests;

// HourlySummary: totals and periods from hourly readings, with missing and not-fetched hours never shown as zero.
public class HourlySummaryTests
{
    // "Now" for the Summarize tests: well after the periods they ask about, unless a test says otherwise.
    private static readonly DateTime Later = new(2026, 11, 21, 12, 0, 0);

    private static readonly MeterSummary Kitchen = Meter(1111, "E350", nickname: "Køkur", address: "Testgøta 1, 100 Tórshavn");
    private static readonly MeterSummary Heat = Meter(2222, "E360 LTE", nickname: null, address: "Testgøta 1, 100 Tórshavn");
    private static readonly MeterSummary Old = Meter(4444, "E200", nickname: null, address: "Testgøta 1, 100 Tórshavn", removed: true);

    [Fact]
    public void Summarize_Hour_RoundsValuesAndAddsTotal()
    {
        var usage = new List<MeterUsage>
        {
            new("2222",
            [
                new(new DateTime(2026, 10, 10, 0, 0, 0), 1.167m, 18184.133m, "kWh"),
                new(new DateTime(2026, 10, 10, 1, 0, 0), 0.7539999999971769m, 18184.887m, "kWh"),
            ]),
        };

        var meter = Assert.Single(HourlySummary.Summarize(
            [Heat], usage, new DateTime(2026, 10, 10, 0, 0, 0), new DateTime(2026, 10, 10, 2, 0, 0), Later, Granularity.Hour));

        Assert.Equal(2222, meter.MeterId);
        Assert.Equal("E360 LTE", meter.MeterName);
        Assert.Equal("kWh", meter.Unit);
        Assert.Equal("hour", meter.Granularity);
        Assert.Equal(1.921m, meter.Total);
        Assert.Equal(2, meter.ExpectedHours);
        Assert.Equal(0, meter.MissingHours);
        Assert.Null(meter.Note);
        Assert.Equal([new SeriesPeriod("2026-10-10T00:00", 1.167m, null), new SeriesPeriod("2026-10-10T01:00", 0.754m, null)], meter.Periods);
    }

    [Fact]
    public void Summarize_Hour_MissingHoursAreListedWithoutValue()
    {
        var readings = Hours(Day(2026, 10, 10), 22);

        var meter = Assert.Single(HourlySummary.Summarize(
            [Heat], [new MeterUsage("2222", readings)], Day(2026, 10, 10), Day(2026, 10, 11), Later, Granularity.Hour));

        Assert.Equal(24, meter.Periods.Count);
        Assert.Equal(new SeriesPeriod("2026-10-10T22:00", null, 1), meter.Periods[22]);
        Assert.Equal(new SeriesPeriod("2026-10-10T23:00", null, 1), meter.Periods[23]);
    }

    [Fact]
    public void Summarize_Day_SumsEachDayAndFlagsIncompleteDays()
    {
        var readings = Hours(Day(2026, 10, 10), 24).Concat(Hours(Day(2026, 10, 11), 20)).ToList();

        var meter = Assert.Single(HourlySummary.Summarize(
            [Heat], [new MeterUsage("2222", readings)], Day(2026, 10, 10), Day(2026, 10, 13), Later, Granularity.Day));

        Assert.Equal(
            [
                new SeriesPeriod("2026-10-10", 24m, null),
                new SeriesPeriod("2026-10-11", 20m, 4),
                new SeriesPeriod("2026-10-12", null, 24),
            ],
            meter.Periods);
        Assert.Equal(44m, meter.Total);
        Assert.Equal(28, meter.MissingHours);
        Assert.Contains("28 of 72 hours", meter.Note);
    }

    [Fact]
    public void Summarize_Month_SumsAcrossMonthBoundary()
    {
        var readings = Hours(Day(2026, 9, 30), 24).Concat(Hours(Day(2026, 10, 1), 24)).ToList();

        var meter = Assert.Single(HourlySummary.Summarize(
            [Heat], [new MeterUsage("2222", readings)], Day(2026, 9, 30), Day(2026, 10, 2), Later, Granularity.Month));

        Assert.Equal([new SeriesPeriod("2026-09", 24m, null), new SeriesPeriod("2026-10", 24m, null)], meter.Periods);
        Assert.Equal(48m, meter.Total);
    }

    [Fact]
    public void Summarize_MeterWithoutReadings_HasNoTotalAndSaysDataIsMissing()
    {
        var meter = Assert.Single(HourlySummary.Summarize(
            [Heat], [new MeterUsage("2222", [])], Day(2026, 10, 10), Day(2026, 10, 11), Later, Granularity.Day));

        Assert.Null(meter.Total);
        Assert.Equal(24, meter.ExpectedHours);
        Assert.Equal(24, meter.MissingHours);
        Assert.Contains("not zero", meter.Note);
        Assert.Equal([new SeriesPeriod("2026-10-10", null, 24)], meter.Periods);
    }

    [Fact]
    public void Summarize_MeterLeftOutBySev_IsReportedAsMissing()
    {
        var results = HourlySummary.Summarize(
            [Kitchen, Heat], [new MeterUsage("2222", Hours(Day(2026, 10, 10), 24))], Day(2026, 10, 10), Day(2026, 10, 11), Later, Granularity.Day);

        Assert.Equal([1111, 2222], results.Select(r => r.MeterId));
        Assert.Null(results[0].Total);
        Assert.Contains("not zero", results[0].Note);
        Assert.Equal(24m, results[1].Total);
    }

    [Fact]
    public void Summarize_Today_OnlyHoursThatHaveEndedAreExpected()
    {
        var now = new DateTime(2026, 11, 21, 10, 30, 0);

        var meter = Assert.Single(HourlySummary.Summarize(
            [Heat], [new MeterUsage("2222", Hours(Day(2026, 11, 21), 10))], Day(2026, 11, 21), Day(2026, 11, 22), now, Granularity.Day));

        Assert.Equal(10, meter.ExpectedHours);
        Assert.Equal(0, meter.MissingHours);
        Assert.Null(meter.Note);
        Assert.Equal([new SeriesPeriod("2026-11-21", 10m, null)], meter.Periods);
    }

    [Fact]
    public void Summarize_FuturePeriod_SaysItHasNotHappened()
    {
        var meter = Assert.Single(HourlySummary.Summarize(
            [Heat], [new MeterUsage("2222", [])], Day(2026, 12, 1), Day(2026, 12, 2), Later, Granularity.Day));

        Assert.Null(meter.Total);
        Assert.Equal(0, meter.ExpectedHours);
        Assert.Contains("hasn't happened", meter.Note);
        Assert.Empty(meter.Periods);
    }

    [Fact]
    public void Summarize_NotFetchedPart_IsReportedSeparatelyFromMissingData()
    {
        // October was fetched, but with 4 hours missing on the 10th; September wasn't fetched yet.
        var readings = Enumerable.Range(0, 31)
            .SelectMany(d => Hours(Day(2026, 10, 1).AddDays(d), d == 9 ? 20 : 24))
            .ToList();

        var meter = Assert.Single(HourlySummary.Summarize(
            [Heat], [new MeterUsage("2222", readings)], Day(2026, 9, 1), Day(2026, 11, 1), Later, Granularity.Month,
            notFetched: (Day(2026, 9, 1), Day(2026, 10, 1))));

        Assert.Equal(
            [new SeriesPeriod("2026-09", null, 720, true), new SeriesPeriod("2026-10", 740m, 4, null)],
            meter.Periods);
        Assert.Contains("from 2026-09-01 up to (not including) 2026-10-01 isn't fetched yet", meter.Note);
        Assert.Contains("Ask again in a few minutes", meter.Note);
        Assert.Contains("4 of 1464 hours have no reading", meter.Note);
    }

    [Fact]
    public void Summarize_NothingFetched_DoesNotClaimDataIsMissing()
    {
        var meter = Assert.Single(HourlySummary.Summarize(
            [Heat], [new MeterUsage("2222", [])], Day(2026, 9, 1), Day(2026, 10, 1), Later, Granularity.Month,
            notFetched: (Day(2026, 9, 1), Day(2026, 10, 1))));

        Assert.Null(meter.Total);
        Assert.Contains("isn't fetched yet", meter.Note);
        Assert.DoesNotContain("not zero", meter.Note);
    }

    [Fact]
    public void Summarize_NotFetchedWithinOneDay_ShowsTheTimes()
    {
        var meter = Assert.Single(HourlySummary.Summarize(
            [Heat], [new MeterUsage("2222", [])], Day(2026, 11, 21), Later, Later, Granularity.Hour,
            notFetched: (Day(2026, 11, 21), Day(2026, 11, 21).AddHours(12))));

        Assert.Contains("from 2026-11-21 00:00 up to (not including) 2026-11-21 12:00 isn't fetched yet", meter.Note);
    }

    [Fact]
    public void Summarize_MissingCost_DoesNotTalkAboutUsage()
    {
        var meter = Assert.Single(HourlySummary.Summarize(
            [Heat], [new MeterCost("2222", [])], Day(2026, 10, 1), Day(2026, 10, 2), Later, Granularity.Day, defaultUnit: "DKR", decimals: 2));

        Assert.Contains("the data is missing, not zero.", meter.Note);
        Assert.DoesNotContain("usage", meter.Note);
    }

    // --- Cost ---

    [Fact]
    public void Summarize_Cost_UsesSevUnitAndRoundsToTwoDecimals()
    {
        var readings = Enumerable.Range(0, 24)
            .Select(h => new CostReading(Day(2026, 10, 10).AddHours(h), 1.3760499999948483m, "DKR", 1.825m, 310))
            .ToList();

        var meter = Assert.Single(HourlySummary.Summarize(
            [Heat], [new MeterCost("2222", readings)], Day(2026, 10, 10), Day(2026, 10, 11), Later, Granularity.Day,
            defaultUnit: "DKR", decimals: 2));

        Assert.Equal("DKR", meter.Unit);
        Assert.Equal(33.03m, meter.Total);
        Assert.Equal(0, meter.MissingHours);
        Assert.Null(meter.Note);
        Assert.Equal([new SeriesPeriod("2026-10-10", 33.03m, null)], meter.Periods);
    }

    [Fact]
    public void Summarize_CostWithoutReadings_UsesDefaultUnitAndSaysDataIsMissing()
    {
        var meter = Assert.Single(HourlySummary.Summarize(
            [Heat], [new MeterCost("2222", [])], Day(2026, 10, 10), Day(2026, 10, 11), Later, Granularity.Day,
            defaultUnit: "DKR", decimals: 2));

        Assert.Equal("DKR", meter.Unit);
        Assert.Null(meter.Total);
        Assert.Equal(24, meter.MissingHours);
        Assert.Contains("missing", meter.Note);
    }

    // --- CO2 ---

    [Fact]
    public void Summarize_Co2_UsesSevUnitAndRoundsToTwoDecimals()
    {
        var readings = Enumerable.Range(0, 24)
            .Select(h => new Co2Reading(Day(2026, 10, 10).AddHours(h), 0.4833089999986543m, "Kg CO2"))
            .ToList();

        var meter = Assert.Single(HourlySummary.Summarize(
            [Heat], [new MeterCo2("2222", readings)], Day(2026, 10, 10), Day(2026, 10, 11), Later, Granularity.Day,
            defaultUnit: "Kg CO2", decimals: 2));

        Assert.Equal("Kg CO2", meter.Unit);
        Assert.Equal(11.6m, meter.Total);
        Assert.Equal(0, meter.MissingHours);
        Assert.Equal([new SeriesPeriod("2026-10-10", 11.6m, null)], meter.Periods);
    }

    // --- Meters installed or removed within the range ---

    [Fact]
    public void Summarize_MeterInstalledInRange_ExpectsHoursFromItsFirstReading()
    {
        // Installed on the 11th, first reading at 08:00.
        var meter = Heat with { InstallDate = new DateOnly(2026, 10, 11) };
        var readings = Hours(Day(2026, 10, 11).AddHours(8), 16).Concat(Hours(Day(2026, 10, 12), 24)).ToList();

        var result = Assert.Single(HourlySummary.Summarize(
            [meter], [new MeterUsage("2222", readings)], Day(2026, 10, 1), Day(2026, 10, 13), Later, Granularity.Day));

        Assert.Equal(40, result.ExpectedHours);
        Assert.Equal(0, result.MissingHours);
        Assert.Null(result.Note);
        Assert.Equal([new SeriesPeriod("2026-10-11", 16m, null), new SeriesPeriod("2026-10-12", 24m, null)], result.Periods);
    }

    [Fact]
    public void Summarize_MeterRemovedInRange_ExpectsHoursUpToItsLastReading()
    {
        // Removed on the 11th, last reading at 11:00.
        var meter = Heat with { RemovalDate = new DateOnly(2026, 10, 11), Removed = true };
        var readings = Hours(Day(2026, 10, 10), 24).Concat(Hours(Day(2026, 10, 11), 12)).ToList();

        var result = Assert.Single(HourlySummary.Summarize(
            [meter], [new MeterUsage("2222", readings)], Day(2026, 10, 10), Day(2026, 11, 1), Later, Granularity.Month));

        Assert.Equal(36, result.ExpectedHours);
        Assert.Equal(0, result.MissingHours);
        Assert.Null(result.Note);
        Assert.Equal([new SeriesPeriod("2026-10", 36m, null)], result.Periods);
    }

    [Fact]
    public void Summarize_InstallDayWithoutReadings_IsExpectedInFull()
    {
        var meter = Heat with { InstallDate = new DateOnly(2026, 10, 11) };

        var result = Assert.Single(HourlySummary.Summarize(
            [meter], [new MeterUsage("2222", [])], Day(2026, 10, 10), Day(2026, 10, 12), Later, Granularity.Day));

        Assert.Equal(24, result.ExpectedHours);
        Assert.Equal(24, result.MissingHours);
        Assert.Contains("not zero", result.Note);
        Assert.Equal([new SeriesPeriod("2026-10-11", null, 24)], result.Periods);
    }

    [Fact]
    public void Summarize_MeterNotInstalledDuringRange_SaysSo()
    {
        var result = Assert.Single(HourlySummary.Summarize(
            [Old], [], Day(2026, 10, 10), Day(2026, 10, 11), Later, Granularity.Day));

        Assert.Null(result.Total);
        Assert.Equal(0, result.ExpectedHours);
        Assert.Equal(0, result.MissingHours);
        Assert.Contains("wasn't installed during this period (installed 2013-03-12, removed 2024-01-01)", result.Note);
        Assert.DoesNotContain("missing", result.Note);
        Assert.Empty(result.Periods);
    }

    private static DateTime Day(int year, int month, int day) => new(year, month, day);

    // `count` readings of 1 kWh, one per hour from 00:00 on `day`.
    private static List<UsageReading> Hours(DateTime day, int count) =>
        Enumerable.Range(0, count).Select(h => new UsageReading(day.AddHours(h), 1m, null, "kWh")).ToList();

    private static MeterSummary Meter(int id, string name, string? nickname, string address, bool removed = false) =>
        new(id, name, "E-01", nickname, address, new DateOnly(2013, 3, 12), removed ? new DateOnly(2024, 1, 1) : null, removed);
}
