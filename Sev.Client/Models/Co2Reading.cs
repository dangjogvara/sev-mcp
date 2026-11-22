using System.Text.Json.Serialization;

namespace Sev.Client.Models;

// One hour from estimated_CO2: Value is the hour's estimated emission in kg CO2 (Unit "Kg CO2").
// SEV also sends cumulative_value, but always null, so it isn't kept.
public record Co2Reading(
    DateTime TimeStamp,
    [property: JsonPropertyName("reading")]
    decimal Value,
    string? Unit
) : IHourlyReading;
