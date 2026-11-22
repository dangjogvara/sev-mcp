using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Sev.Client;
using Sev.McpServer.Tools;

namespace Sev.McpServer.Tests;

// get_usage across the top of an hour: the tool must take "now" before fetching, so it never expects an hour
// that SevDataService (reading the clock later) didn't fetch yet, and wrongly report it as missing.
public class UsageToolsClockTests
{
    private const string Meters = """
        [{"customer_name":"Test","installations":[{"move_in_date":"2019-06-01T00:00:00","inst_id":1,"address":"Testgøta 1",
          "postal_code":"100","postal_district":"Tórshavn","tarif_number":310,"billing_type":"OS",
          "meters":[{"meter_id":1111,"serial_number":"1","meter_name":"E360","meter_type":"E-01","install_date":"2020-01-01"}]}]}]
        """;

    [Fact]
    public async Task CallCrossingTheHour_ReportsNoMissingHours()
    {
        // The clock is read three times during the call (winter: Faroese time is UTC). The first two readings
        // are just before 13:00, the third just after. Taken first, the tool's "now" is before 13:00 and it
        // doesn't expect the 12:00 hour; read last (the old order), it would expect 12:00 while SevDataService,
        // which read the clock before 13:00, never asked SEV for it.
        var beforeTheHour = new DateTimeOffset(2026, 11, 21, 12, 59, 59, TimeSpan.Zero);
        var clock = new SteppingClock(beforeTheHour, beforeTheHour, beforeTheHour.AddSeconds(2));
        var handler = new CannedSev(Meters);
        var data = new SevDataService(new SevClient(new HttpClient(handler) { BaseAddress = SevClient.DefaultBaseAddress }), new SevCache(clock));
        var tools = new UsageTools(data, clock);

        var meter = Assert.Single(await tools.GetUsageAsync("2026-11-21", "2026-11-22", "day"));

        Assert.Equal(0, meter.MissingHours);
        Assert.Null(meter.Note);
    }


    // Returns the given times in order, then keeps returning the last one.
    private sealed class SteppingClock(params DateTimeOffset[] times) : TimeProvider
    {
        private int _next;

        public override DateTimeOffset GetUtcNow() => times[Math.Min(_next++, times.Length - 1)];
    }

    // Answers the meter list with canned JSON, and usage like SEV does: 1 kWh for every hour in
    // [from_date, to_date) of the request, so an hour that isn't asked for isn't returned.
    private sealed class CannedSev(string meters) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body;
            if (request.RequestUri!.AbsolutePath.EndsWith("get_available_meters"))
            {
                body = meters;
            }
            else
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var from = DateTime.Parse(json.RootElement.GetProperty("from_date").GetString()!, CultureInfo.InvariantCulture);
                var to = DateTime.Parse(json.RootElement.GetProperty("to_date").GetString()!, CultureInfo.InvariantCulture);
                var readings = Enumerable.Range(0, (int)(to - from).TotalHours).Select(h =>
                    $"{{\"time_stamp\":\"{from.AddHours(h):yyyy-MM-ddTHH:mm:ss}\",\"reading\":1.0,\"cumulative_value\":null,\"unit\":\"kWh\"}}");
                body = "[{\"meter_id\":\"1111\",\"readings\":[" + string.Join(",", readings) + "]}]";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
