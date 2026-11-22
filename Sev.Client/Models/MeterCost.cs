namespace Sev.Client.Models;

public record MeterCost(
    string MeterId,
    IReadOnlyList<CostReading> Readings
) : IMeterReadings
{
    IReadOnlyList<IHourlyReading> IMeterReadings.Readings => Readings;
}
