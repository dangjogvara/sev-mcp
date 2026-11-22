namespace Sev.Client.Models;

// Address, PostalCode and PostalDistrict are nullable in SEV's spec, so they may be missing.
public record Installation(
    DateTime MoveInDate,
    DateTime? MoveOnDate,
    int InstId,
    string? InstNickname,
    string? Address,
    string? Floor,
    string? PostalCode,
    string? PostalDistrict,
    int TarifNumber,
    string BillingType,
    IReadOnlyList<Meter> Meters
);