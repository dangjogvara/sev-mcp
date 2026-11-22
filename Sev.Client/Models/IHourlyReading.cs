namespace Sev.Client.Models;

// What every hourly reading has, whatever it measures (kWh, DKK, kg CO2), so caching and
// summarizing can be written once for all of them.
public interface IHourlyReading
{
    DateTime TimeStamp { get; }
    decimal Value { get; }
    string? Unit { get; }
}
