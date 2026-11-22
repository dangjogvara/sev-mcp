namespace Sev.Client.Models;

public record MeterUsage(
    string MeterId,
    IReadOnlyList<UsageReading> Readings
) : IMeterReadings
{
    IReadOnlyList<IHourlyReading> IMeterReadings.Readings => Readings;
}
