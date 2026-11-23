using Sev.Client;
using Sev.Client.Models;
using Sev.Core;

namespace Sev.Core.Tests;

public class CostPricesTests
{
    private static readonly DateTime Start = new(2026, 11, 1);

    [Fact]
    public void OnePrice_IsOneStretchCoveringTheReadings()
    {
        var prices = CostPrices.For(Hours(Start, 48, price: 1.825m));

        Assert.Equal([new PricePeriod("2026-11-01T00:00", "2026-11-03T00:00", 1.825m, 310)], prices);
    }

    [Fact]
    public void PriceChange_SplitsTheStretch()
    {
        var readings = Hours(Start, 24, price: 1.7375m).Concat(Hours(Start.AddDays(1), 24, price: 1.825m)).ToList();

        var prices = CostPrices.For(readings);

        Assert.Equal(
            [
                new PricePeriod("2026-11-01T00:00", "2026-11-02T00:00", 1.7375m, 310),
                new PricePeriod("2026-11-02T00:00", "2026-11-03T00:00", 1.825m, 310),
            ],
            prices);
    }

    [Fact]
    public void TariffChange_AlsoSplits_AndOrderDoesNotMatter()
    {
        var readings = Hours(Start.AddHours(12), 12, price: 1.825m, tariff: 320).Concat(Hours(Start, 12, price: 1.825m)).ToList();

        var prices = CostPrices.For(readings)!;

        Assert.Equal([310, 320], prices.Select(p => p.Tariff));
        Assert.Equal("2026-11-01T12:00", prices[0].To);
    }

    [Fact]
    public void NoReadings_NoPrices()
    {
        Assert.Null(CostPrices.For([]));
    }

    [Fact]
    public void WithPrices_TakesTheMetersOwnReadings()
    {
        var result = new CostResult(
            [new MeterCost("1111", Hours(Start, 24, price: 2.325m)), new MeterCost("2222", Hours(Start, 24, price: 1.825m))], null);
        var summary = new MeterSeriesResult(2222, "E360", "DKR", 10m, 24, 0, null, "day", []);

        var withPrices = CostPrices.WithPrices(summary, result);

        Assert.Equal(1.825m, Assert.Single(withPrices.Prices!).PricePerKwh);
        Assert.Null(CostPrices.WithPrices(summary with { MeterId = 3333 }, result).Prices);
    }

    private static List<CostReading> Hours(DateTime start, int count, decimal price, int tariff = 310) =>
        Enumerable.Range(0, count).Select(h => new CostReading(start.AddHours(h), price, "DKR", price, tariff)).ToList();
}
