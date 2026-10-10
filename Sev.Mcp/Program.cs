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

Console.WriteLine(customers);

var rows = customers
        .SelectMany(c => c.Installations)
        .SelectMany(i => i.Meters, (i, m) => new { m.MeterId, m.MeterName, i.Address });

foreach (var r in rows)
{
    Console.WriteLine($"{r.MeterId} {r.MeterName} {r.Address}");
}