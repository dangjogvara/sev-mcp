using System.Text.Json.Serialization;
using MyUplink.Client.Json;

namespace MyUplink.Client.Models;

// The shapes of myUplink's answers (https://api.myuplink.com/swagger/docs/public-v2/swagger.json), with
// only the fields the tools use. myUplink sends camelCase JSON, read with MyUplinkClient.JsonOptions.

// One installation ("system") on the account, with its devices. HasAlarm is true while an alarm is active.
public record MyUplinkSystem(
    string SystemId,
    string? Name,
    bool HasAlarm,
    IReadOnlyList<MyUplinkDevice>? Devices);

// A device in a system, e.g. the heat pump. ConnectionState is "Connected" or "Disconnected".
public record MyUplinkDevice(
    string Id,
    string? ConnectionState,
    string? CurrentFwVersion,
    MyUplinkProduct? Product);

public record MyUplinkProduct(string? SerialNumber, string? Name);

// One parameter of a device with its current value. StrVal is the value as the myUplink app shows it, with
// the unit or, for coded values (status, mode), the label from EnumValues. Writable parameters are settings.
// Timestamp is when the value last changed, not when it was last read.
public record DataPoint(
    string? Category,
    [property: JsonConverter(typeof(StringOrNumberConverter))]
    string ParameterId,
    string? ParameterName,
    string? ParameterUnit,
    bool Writable,
    DateTimeOffset? Timestamp,
    double? Value,
    string? StrVal,
    IReadOnlyList<EnumValue>? EnumValues);

public record EnumValue(string? Value, string? Text);

// An active alarm. Severity and AlarmNumber are the manufacturer's codes; Header and Description are text.
public record MyUplinkAlarm(
    int AlarmNumber,
    string? DeviceId,
    int Severity,
    string? CreatedDatetime,
    string? Header,
    string? Description,
    string? EquipName);

internal record SystemsPage(IReadOnlyList<MyUplinkSystem>? Systems);

internal record AlarmsPage(IReadOnlyList<MyUplinkAlarm>? Notifications);

internal record TokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);
