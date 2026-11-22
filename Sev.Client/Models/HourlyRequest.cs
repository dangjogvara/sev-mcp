namespace Sev.Client.Models;

// The request body of every hourly endpoint (usage, cost, CO2): which meters, and [FromDate, ToDate) in Faroese time.
internal record HourlyRequest(
    IReadOnlyList<int> Meters,
    DateTime FromDate,
    DateTime ToDate
);
