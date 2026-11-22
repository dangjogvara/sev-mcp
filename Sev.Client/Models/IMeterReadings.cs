namespace Sev.Client.Models;

// One meter's hourly readings, whatever they measure. MeterUsage and MeterCost implement it.
public interface IMeterReadings
{
    string MeterId { get; }
    IReadOnlyList<IHourlyReading> Readings { get; }
}
