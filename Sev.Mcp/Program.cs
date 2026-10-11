using Sev.Client;

var user = Environment.GetEnvironmentVariable("SEV_USER")
    ?? throw new InvalidOperationException("SEV_USER not set");
var key = Environment.GetEnvironmentVariable("SEV_KEY")
    ?? throw new InvalidOperationException("SEV_KEY not set");

var http = new HttpClient
{
    BaseAddress = new Uri("https://api.sev.fo/api/CustomerRESTApi/")
};
var client = new SevClient(http);

await client.LoginAsync(user, key);
var customers = await client.GetAvailableMetersAsync();

var rows = customers
        .SelectMany(c => c.Installations)
        .SelectMany(i => i.Meters, (i, m) => new { m.MeterId, m.MeterName, i.Address });

foreach (var r in rows)

{
    Console.WriteLine($"{r.MeterId} {r.MeterName} {r.Address}");
}

var usage = await client.GetHourlyUsageAsync(
    [7842, 71571],
    new DateTime(2026, 10, 10),
    new DateTime(2026, 10, 11));

foreach (var meter in usage)
{
    Console.WriteLine($"Meter {meter.MeterId}: {meter.Readings.Count} readings");
    foreach (var r in meter.Readings)
        Console.WriteLine($"  {r.TimeStamp:HH:mm}  {r.Value:0.###} {r.Unit}");
}