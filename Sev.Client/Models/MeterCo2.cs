namespace Sev.Client.Models;

public record MeterCo2(
    string MeterId,
    IReadOnlyList<Co2Reading> Readings
) : IMeterReadings
{
    IReadOnlyList<IHourlyReading> IMeterReadings.Readings => Readings;
}
