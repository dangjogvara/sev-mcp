using System.ComponentModel;
using System.Globalization;
using System.Net;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using MyUplink.Client;
using MyUplink.Client.Models;
using Sev.Client;

namespace Sev.McpServer.Tools;

// Heat pump status from myUplink. Only registered when the MyUplink section is configured (see MyUplinkServices).
[McpServerToolType]
public class HeatPumpTools(MyUplinkClient myUplink)
{
    // myUplink reports this when a sensor isn't connected.
    public const double NotConnected = -32768;

    [McpServerTool(Name = "get_heatpump_status", Title = "Get heat pump status", ReadOnly = true, Idempotent = true)]
    [Description(
        "Gets the current state of the user's heat pump(s) through myUplink: temperatures (outdoor, heating flow and return, " +
        "hot water), what the heat pump is doing (status), compressor speed, immersion heater, settings such as setpoints " +
        "(marked setting: true), and active alarms. Outdoor temperature is the heat pump's own sensor. " +
        "Values are current; past readings come from get_heatpump_history. Electricity use comes from get_usage, not from here. " +
        "Read-only: it can't change settings. Values are grouped by the manufacturer's categories, in their English names.")]
    public Task<IReadOnlyList<HeatPumpSystemResult>> GetHeatPumpStatusAsync(CancellationToken cancellationToken) =>
        CallAsync<IReadOnlyList<HeatPumpSystemResult>>(async () =>
        {
            var results = new List<HeatPumpSystemResult>();
            foreach (var system in await myUplink.GetSystemsAsync(cancellationToken))
            {
                var points = new Dictionary<string, IReadOnlyList<DataPoint>>();
                foreach (var device in system.Devices ?? [])
                {
                    points[device.Id] = await myUplink.GetPointsAsync(device.Id, cancellationToken);
                }
                // Alarms only cost a call when the system says it has one.
                var alarms = system.HasAlarm ? await myUplink.GetActiveAlarmsAsync(system.SystemId, cancellationToken) : [];
                results.Add(Summarize(system, points, alarms));
            }
            return results.Count > 0
                ? results
                : throw new McpException("myUplink lists no heat pumps on this account.");
        });

    // Turns myUplink's answers into the tool's result: values grouped by category, in the order myUplink sends
    // them, leaving out parameters without an English name and sensors that aren't connected. Coded values
    // (status, mode) are given as their label in Text, everything else as a number with its unit.
    public static HeatPumpSystemResult Summarize(
        MyUplinkSystem system, IReadOnlyDictionary<string, IReadOnlyList<DataPoint>> pointsByDevice, IReadOnlyList<MyUplinkAlarm> alarms)
    {
        var devices = (system.Devices ?? [])
            .Select(device =>
            {
                var points = pointsByDevice.TryGetValue(device.Id, out var p) ? p : [];
                var categories = points
                    .Where(IsUseful)
                    .GroupBy(point => HasName(point.Category) ? point.Category! : "Other")
                    .Select(group => new HeatPumpCategory(group.Key, group.Select(ToValue).ToList()))
                    .ToList();
                var offline = device.ConnectionState is { } state && !state.Equals("Connected", StringComparison.OrdinalIgnoreCase);
                return new HeatPumpDeviceResult(
                    device.Product?.Name,
                    device.ConnectionState,
                    categories,
                    offline ? "The device isn't connected to myUplink, so these are the last values it reported." : null);
            })
            .ToList();

        return new HeatPumpSystemResult(
            system.Name,
            system.HasAlarm,
            alarms.Select(a => new HeatPumpAlarm(a.Header, a.Description, a.Severity, LocalTime(a.CreatedDatetime), a.EquipName)).ToList(),
            devices);
    }

    private static bool IsUseful(DataPoint point) => HasName(point.ParameterName) && point.Value != NotConnected;

    // Some firmware lacks English texts, and myUplink then sends "Text not found: id[], fw[...], lang[en-GB]"
    // as the name of the parameter or of its category.
    internal static bool HasName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && !name.StartsWith("Text not found", StringComparison.Ordinal);

    private static HeatPumpValue ToValue(DataPoint point)
    {
        var setting = point.Writable ? true : (bool?)null;
        if (point.EnumValues is { Count: > 0 } && !string.IsNullOrWhiteSpace(point.StrVal))
        {
            return new HeatPumpValue(point.ParameterName!, null, point.StrVal, null, setting);
        }
        var unit = string.IsNullOrWhiteSpace(point.ParameterUnit) ? null : point.ParameterUnit;
        return new HeatPumpValue(point.ParameterName!, point.Value, null, unit, setting);
    }

    // Alarm times in Faroese local time like everything else the tools return; left as sent if unreadable.
    private static string? LocalTime(string? timestamp) =>
        DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? TimeZoneInfo.ConvertTime(time, SevTime.Zone).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture)
            : timestamp;

    // Only an McpException's message reaches Claude, so myUplink failures are turned into ones it can act on.
    public static async Task<T> CallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (MyUplinkApiException ex)
        {
            throw new McpException(Describe(ex), ex);
        }
    }

    public static string Describe(MyUplinkApiException ex) => ex.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest =>
            "myUplink rejected the server's application credentials (MyUplink client ID or secret), or refused access. They need to be checked.",
        HttpStatusCode.TooManyRequests =>
            "myUplink allows 25 calls per minute and they are used up."
            + (ex.RetryAfter is { } wait ? $" Try again in {Math.Ceiling(wait.TotalSeconds)} seconds." : " Try again in a minute."),
        _ => ex.Message,
    };
}

// One heat pump installation. HasAlarm is myUplink's flag; Alarms lists the active ones.
public record HeatPumpSystemResult(
    string? Name,
    bool HasAlarm,
    IReadOnlyList<HeatPumpAlarm> Alarms,
    IReadOnlyList<HeatPumpDeviceResult> Devices);

// Since is when the alarm was raised, in Faroese local time.
public record HeatPumpAlarm(string? Header, string? Description, int Severity, string? Since, string? Equipment);

public record HeatPumpDeviceResult(
    string? Product,
    string? ConnectionState,
    IReadOnlyList<HeatPumpCategory> Categories,
    string? Note);

public record HeatPumpCategory(string Category, IReadOnlyList<HeatPumpValue> Values);

// Value with Unit for measurements, Text for coded values (status, mode). Setting is true for values that are
// configuration (setpoints, modes), null for readings.
public record HeatPumpValue(string Name, double? Value, string? Text, string? Unit, bool? Setting);
