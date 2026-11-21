namespace Sev.Client.Models;

internal record UsageRequest(
    IReadOnlyList<int> Meters,
    DateTime FromDate,
    DateTime ToDate
);