using System.Globalization;
using Sev.Client;
using Sev.Client.Models;

namespace Sev.Core;

// The price per kWh behind get_cost. SEV's price is fixed per meter (the same every hour of the day) and only
// changes now and then, so instead of a price per hour, each meter gets the stretches of the period with one
// price and tariff: usually one, more when SEV changed the price within the period.
public static class CostPrices
{
    // Consecutive readings with the same price and tariff make one stretch, from the first reading's hour up to
    // (not including) the hour after the last. Null without readings.
    public static IReadOnlyList<PricePeriod>? For(IReadOnlyList<CostReading> readings)
    {
        if (readings.Count == 0)
        {
            return null;
        }
        var periods = new List<PricePeriod>();
        var ordered = readings.OrderBy(r => r.TimeStamp).ToList();
        var start = ordered[0];
        var last = ordered[0];
        foreach (var reading in ordered.Skip(1))
        {
            if (reading.PriceUnit != start.PriceUnit || reading.Tarif != start.Tarif)
            {
                periods.Add(Period(start, last));
                start = reading;
            }
            last = reading;
        }
        periods.Add(Period(start, last));
        return periods;
    }

    // Adds the meter's prices to its get_cost result, from the cost readings SEV returned for it.
    public static MeterSeriesResult WithPrices(MeterSeriesResult summary, IHourlyResult result)
    {
        var meter = result.Meters.OfType<MeterCost>().FirstOrDefault(m => m.MeterId == summary.MeterId.ToString(CultureInfo.InvariantCulture));
        return summary with { Prices = For(meter?.Readings ?? []) };
    }

    private static PricePeriod Period(CostReading first, CostReading last) =>
        new(Hour(first.TimeStamp), Hour(last.TimeStamp.AddHours(1)), first.PriceUnit, first.Tarif);

    private static string Hour(DateTime time) => time.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
}

// A stretch of the period with one price: PricePerKwh in DKK, Tariff as SEV numbers it, in force from From up to
// (not including) To, in Faroese local time.
public record PricePeriod(string From, string To, decimal PricePerKwh, int Tariff);
