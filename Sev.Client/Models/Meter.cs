using System.Text.Json.Serialization;
using Sev.Client.Json;

namespace Sev.Client.Models;

public record Meter(
    int MeterId,
    [property: JsonConverter(typeof(LenientStringConverter))]
    string? MeterNickname,
    string SerialNumber,
    string MeterName,
    string MeterType,
    [property: JsonConverter(typeof(LenientDateOnlyConverter))]
    DateOnly? InstallDate,
    [property: JsonConverter(typeof(LenientDateOnlyConverter))]
    DateOnly? RemovalDate
);