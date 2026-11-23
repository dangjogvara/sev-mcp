using System.Net;
using System.Text;
using ModelContextProtocol;
using MyUplink.Client;
using Sev.Core;
using Sev.Core.HeatPump;
using Sev.McpServer.HeatPump;
using Sev.McpServer.Tools;

namespace Sev.McpServer.Tests;

// The hourly logger and get_heatpump_history; the log itself is tested in Sev.Core.Tests.HeatPumpLogTests.
public class HeatPumpHistoryTests
{
    private static readonly DateTime Hour = new(2026, 11, 22, 18, 0, 0);

    // --- Sampling ---

    [Fact]
    public async Task TakeSample_LogsEveryDeviceForTheHour()
    {
        var log = new HeatPumpLog();
        var client = ClientAnswering(
            """{"systems":[{"systemId":"SYS1","name":"Home","hasAlarm":false,"devices":[{"id":"DEV1","connectionState":"Connected","product":{"name":"CTC"}}]}]}""",
            """[{"category":"General","parameterId":"62000","parameterName":"Outdoor Temperature","parameterUnit":"°C","writable":false,"value":5.5}]""");

        var devices = await HeatPumpLogger.TakeSampleAsync(client, log, Hour, CancellationToken.None);

        Assert.Equal(1, devices);
        var device = Assert.Single(log.Snapshot());
        Assert.Equal("CTC", device.Product);
        Assert.Equal(5.5, Assert.Single(device.Samples).Values["62000"]);
    }

    // --- get_heatpump_history ---

    [Fact]
    public void History_DayAveragesWithMinMaxAndMissingHours()
    {
        var log = new HeatPumpLog();
        // Logging started 22:00 on the 20th; the 21st has 23 of 24 hours (10:00 missing).
        log.Add("DEV1", "CTC", new DateTime(2026, 11, 20, 22, 0, 0), [Outdoor(2)]);
        log.Add("DEV1", "CTC", new DateTime(2026, 11, 20, 23, 0, 0), [Outdoor(4)]);
        for (var h = 0; h < 24; h++)
        {
            if (h != 10)
            {
                log.Add("DEV1", "CTC", new DateTime(2026, 11, 21, h, 0, 0), [Outdoor(h < 12 ? 1 : 7)]);
            }
        }

        var result = Assert.Single(HeatPumpHistoryTools.Summarize(
            log.Snapshot(), new DateTime(2026, 11, 20), new DateTime(2026, 11, 22), new DateTime(2026, 11, 22, 12, 0, 0), Granularity.Day, null));

        Assert.Equal("2026-11-20T22:00", result.LoggedFrom);
        Assert.Contains("Logging started 2026-11-20 22:00", result.Note);
        Assert.Contains("1 of 26 hours", result.Note);
        var series = Assert.Single(result.Series);
        Assert.Equal("Outdoor Temperature", series.Name);
        Assert.Equal(new HeatPumpPeriod("2026-11-20", null, 3, 2, 4, null), series.Periods[0]);
        Assert.Equal(new HeatPumpPeriod("2026-11-21", null, Math.Round((11 * 1 + 12 * 7) / 23.0, 2), 1, 7, 1), series.Periods[1]);
    }

    [Fact]
    public void History_HourValues()
    {
        var log = new HeatPumpLog();
        log.Add("DEV1", "CTC", Hour, [Outdoor(5.25), Reading("62011", "Primary flow temp", 39)]);
        log.Add("DEV1", "CTC", Hour.AddHours(1), [Outdoor(4.75), Reading("62011", "Primary flow temp", 41)]);

        var result = Assert.Single(HeatPumpHistoryTools.Summarize(
            log.Snapshot(), Hour, Hour.AddHours(2), Hour.AddHours(1).AddMinutes(10), Granularity.Hour, ["flow"]));

        var series = Assert.Single(result.Series);
        Assert.Equal("Primary flow temp", series.Name);
        Assert.Equal([39.0, 41.0], series.Periods.Select(p => p.Value!.Value));
        Assert.Null(result.Note);
    }

    [Fact]
    public void History_AllAndUnknownNames()
    {
        var log = new HeatPumpLog();
        log.Add("DEV1", "CTC", Hour, [Outdoor(5), Reading("62011", "Primary flow temp", 39)]);
        var devices = log.Snapshot();

        var all = Assert.Single(HeatPumpHistoryTools.Summarize(devices, Hour, Hour.AddHours(1), Hour, Granularity.Hour, ["all"]));
        var ex = Assert.Throws<McpException>(() =>
            HeatPumpHistoryTools.Summarize(devices, Hour, Hour.AddHours(1), Hour, Granularity.Hour, ["humidity"]));

        Assert.Equal(2, all.Series.Count);
        Assert.Contains("Primary flow temp", ex.Message);
    }

    [Fact]
    public void History_NothingLoggedYet()
    {
        var ex = Assert.Throws<McpException>(() =>
            HeatPumpHistoryTools.Summarize(new HeatPumpLog().Snapshot(), Hour, Hour.AddDays(1), Hour, Granularity.Day, null));

        Assert.Contains("Nothing is logged yet", ex.Message);
    }

    private static LoggedReading Outdoor(double value) => Reading("62000", "Outdoor Temperature", value, "°C");

    private static LoggedReading Reading(string id, string name, double value, string? unit = "°C") =>
        new(id, name, unit, "General", value);

    // A MyUplinkClient whose network answers the token, systems and points requests with canned JSON.
    private static MyUplinkClient ClientAnswering(string systemsJson, string pointsJson)
    {
        var handler = new CannedHandler(path => path switch
        {
            "/oauth/token" => """{"access_token":"t","expires_in":3600}""",
            "/v2/systems/me" => systemsJson,
            _ when path.EndsWith("/points") => pointsJson,
            _ => throw new InvalidOperationException("Unexpected request " + path),
        });
        var tokens = new MyUplinkTokenProvider(new HttpClient(handler) { BaseAddress = MyUplinkClient.DefaultBaseAddress }, "id", "secret");
        return new MyUplinkClient(new HttpClient(handler) { BaseAddress = MyUplinkClient.DefaultBaseAddress }, tokens);
    }

    private sealed class CannedHandler(Func<string, string> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(answer(request.RequestUri!.AbsolutePath), Encoding.UTF8, "application/json"),
            });
    }
}
