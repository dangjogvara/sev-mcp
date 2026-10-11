namespace Sev.Client.Models;

public record HourlyUsageRequest(
    IReadOnlyList<int> Meters,
    DateTime FromDate,
    DateTime ToDate
);