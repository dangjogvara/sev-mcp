using Sev.Client;

var user = Environment.GetEnvironmentVariable("SEV_USER")
    ?? throw new InvalidOperationException("SEV_USER not set");
var key = Environment.GetEnvironmentVariable("SEV_KEY")
    ?? throw new InvalidOperationException("SEV_KEY not set");

var limiter = new SevRateLimiter();
var loginHttp = new HttpClient(new SevRateLimitHandler(limiter) { InnerHandler = new HttpClientHandler() })
{
    BaseAddress = SevClient.DefaultBaseAddress
};
var tokens = new SevTokenProvider(loginHttp, user, key);
var http = new HttpClient(new SevAuthHandler(tokens) { InnerHandler = new SevRateLimitHandler(limiter) { InnerHandler = new HttpClientHandler() } })
{
    BaseAddress = SevClient.DefaultBaseAddress
};
var client = new SevClient(http);

var customers = await client.GetAvailableMetersAsync();

var rows = customers
    .SelectMany(c => c.Installations)
    .SelectMany(i => i.Meters, (i, m) => new { m.MeterId, m.MeterName, i.Address })
    .ToList();

foreach (var r in rows)
{
    Console.WriteLine($"{r.MeterId} {r.MeterName} {r.Address}");
}

var usage = await client.GetHourlyUsageAsync(
    rows.Select(r => r.MeterId).ToList(),
    new DateTime(2026, 10, 10),
    new DateTime(2026, 10, 11));

foreach (var meter in usage)
{
    Console.WriteLine($"Meter {meter.MeterId}: {meter.Readings.Count} readings");
    foreach (var r in meter.Readings)
        Console.WriteLine($"  {r.TimeStamp:HH:mm}  {r.Value:0.###} {r.Unit}");
}
