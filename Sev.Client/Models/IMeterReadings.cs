namespace Sev.Client.Models;

// One meter's hourly readings, whatever they measure. MeterUsage, MeterCost and MeterCo2 implement it.
public interface IMeterReadings
{
    string MeterId { get; }
    IReadOnlyList<IHourlyReading> Readings { get; }
}
