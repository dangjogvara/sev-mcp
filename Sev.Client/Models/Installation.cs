namespace Sev.Client.Models;

public record Installation(
    DateTime MoveInDate,
    DateTime? MoveOnDate,
    int InstId,
    string? InstNickname,
    string Address,
    string? Floor,
    string PostalCode,
    string PostalDistrict,
    int TarifNumber,
    string BillingType,
    List<Meter> Meters
);