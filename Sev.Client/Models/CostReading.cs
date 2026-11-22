using System.Text.Json.Serialization;

namespace Sev.Client.Models;

// One hour from estimated_cost: Value is the hour's cost (Unit "DKR"), PriceUnit the price per kWh,
// Tarif the installation's tariff number.
public record CostReading(
    DateTime TimeStamp,
    [property: JsonPropertyName("reading")]
    decimal Value,
    string? Unit,
    decimal PriceUnit,
    int Tarif
) : IHourlyReading;
