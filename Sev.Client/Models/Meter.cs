namespace Sev.Client.Models;

public record Meter(
    int MeterId,
    string? MeterNickname,
    string SerialNumber,
    string MeterName,
    string MeterType,
    DateOnly InstallDate,
    DateOnly? RemovalDate
);