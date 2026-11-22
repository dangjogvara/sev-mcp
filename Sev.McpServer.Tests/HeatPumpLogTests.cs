using System.Net;
using System.Text;
using ModelContextProtocol;
using MyUplink.Client;
using MyUplink.Client.Models;
using Sev.McpServer.HeatPump;
using Sev.McpServer.Tools;

namespace Sev.McpServer.Tests;

// The hourly heat pump log: what's sampled, how it's stored, and how get_heatpump_history reads it back.
public class HeatPumpLogTests : IDisposable
{
    private static readonly DateTime Hour = new(2026, 11, 22, 18, 0, 0);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sev-mcp-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _messages = [];

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // --- The log ---

    [Fact]
    public void Add_RecordsTheHourOnce()
    {
        var log = new HeatPumpLog();

        log.Add("DEV1", "CTC", Hour, [Outdoor(5)]);
        log.Add("DEV1", "CTC", Hour, [Outdoor(9)]);

        Assert.True(log.HasSample(Hour));
        Assert.False(log.HasSample(Hour.AddHours(1)));
        var sample = Assert.Single(Assert.Single(log.Snapshot()).Samples);
        Assert.Equal(5, sample.Values["62000"]);
    }

    [Fact]
    public void Saved_OneFilePerMonth_LoadedAfterRestart()
    {
        var log = new HeatPumpLog(_directory);
        log.Add("DEV1", "CTC", new DateTime(2026, 10, 31, 23, 0, 0), [Outdoor(4)]);
        log.Add("DEV1", "CTC", new DateTime(2026, 11, 1, 0, 0, 0), [Outdoor(3)]);

        var restarted = new HeatPumpLog(_directory, _messages.Add);

        Assert.Equal(["HeatPumpLog-2026-10.json", "HeatPumpLog-2026-11.json"],
            Directory.GetFiles(_directory).Select(Path.GetFileName).Order());
        var device = Assert.Single(restarted.Snapshot());
        Assert.Equal("CTC", device.Product);
        Assert.Equal([4.0, 3.0], device.Samples.Select(s => s.Values["62000"]));
        Assert.Equal("Outdoor Temperature", Assert.Single(device.Parameters).Name);
        Assert.Contains(_messages, m => m.StartsWith("Loaded 2 logged heat pump hours"));
    }

    [Fact]
    public void UnreadableFile_IsIgnored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "HeatPumpLog-2026-11.json"), "not json");

        var log = new HeatPumpLog(_directory, _messages.Add);

        Assert.Empty(log.Snapshot());
        Assert.Contains(_messages, m => m.StartsWith("Ignoring"));
    }

    [Fact]
    public void WithoutDirectory_NothingIsWritten()
    {
        var log = new HeatPumpLog(null);

        log.Add("DEV1", "CTC", Hour, [Outdoor(5)]);

        Assert.False(Directory.Exists(_directory));
        Assert.True(log.HasSample(Hour));
    }

    // --- Sampling ---

    [Fact]
    public void Measurements_KeepNamedConnectedReadingsOnly()
    {
        var points = new[]
        {
            Point("62000", "Outdoor Temperature", 5, "°C"),
            Point("62003", "Hot water flow temp", HeatPumpTools.NotConnected, "°C"),
            Point("61502", "Hot water setpoint", 50, "°C", writable: true),
            Point("62005", "Status", 4, labels: [new EnumValue("4", "HC")]),
            Point("62002", "Text not found: id[], fw[CTC-01-01], lang[en-GB]", 0),
            Point("62193", "RPS", 35, ""),
            Point("62244", "Software version", 20230301),
        };

        var readings = HeatPumpLogger.Measurements(points);

        Assert.Equal(["62000", "62193"], readings.Select(r => r.ParameterId));
        Assert.Null(readings[1].Unit);
    }

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

    private static DataPoint Point(
        string id, string name, double value, string? unit = null, bool writable = false, IReadOnlyList<EnumValue>? labels = null) =>
        new("General", id, name, unit, writable, null, value, null, labels);

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
