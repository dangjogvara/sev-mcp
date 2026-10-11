using System.Text.Json.Serialization;

namespace Sev.Client.Models;

public record UsageReading(
    DateTime TimeStamp,
    [property: JsonPropertyName("reading")]
    decimal Value,
    decimal? CumulativeValue,
    string Unit
);