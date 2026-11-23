using MyUplink.Client.Models;

namespace Sev.Core.HeatPump;

// Which of myUplink's values are worth showing or logging, in one place.
// - get_heatpump_status shows every named, connected value (ForStatus), settings and status codes included.
// - The hourly log keeps only measurements (ForLog): no settings, no coded values, no software versions.
public static class HeatPumpReadings
{
    // myUplink reports this when a sensor isn't connected.
    public const double NotConnected = -32768;

    // Some firmware lacks English texts, and myUplink then sends "Text not found: id[], fw[...], lang[en-GB]"
    // as the name of the parameter or of its category.
    public static bool HasName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && !name.StartsWith("Text not found", StringComparison.Ordinal);

    // A device that has lost its connection to myUplink ("Disconnected") still has values: the last ones it
    // reported, which aren't readings of now. A device without a state counts as connected.
    public static bool IsOffline(MyUplinkDevice device) =>
        device.ConnectionState is { } state && !state.Equals("Connected", StringComparison.OrdinalIgnoreCase);

    public static bool ForStatus(DataPoint point) => HasName(point.ParameterName) && point.Value != NotConnected;

    // Settings, coded values (status, mode) and software versions (numbers, but not measurements) are left
    // out; get_heatpump_status shows them as they are now.
    public static IReadOnlyList<LoggedReading> ForLog(IReadOnlyList<DataPoint> points) =>
        points
            .Where(p => ForStatus(p)
                && p.Value is not null
                && !p.Writable
                && p.EnumValues is not { Count: > 0 }
                && !p.ParameterName!.Contains("version", StringComparison.OrdinalIgnoreCase))
            .Select(p => new LoggedReading(
                p.ParameterId,
                p.ParameterName!,
                string.IsNullOrWhiteSpace(p.ParameterUnit) ? null : p.ParameterUnit,
                HasName(p.Category) ? p.Category : null,
                p.Value!.Value))
            .ToList();
}
