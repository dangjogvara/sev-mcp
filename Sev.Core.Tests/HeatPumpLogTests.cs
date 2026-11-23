using MyUplink.Client.Models;
using Sev.Core.HeatPump;

namespace Sev.Core.Tests;

// The hourly heat pump log: what's kept from myUplink's values and how it's stored.
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
            Point("62003", "Hot water flow temp", HeatPumpReadings.NotConnected, "°C"),
            Point("61502", "Hot water setpoint", 50, "°C", writable: true),
            Point("62005", "Status", 4, labels: [new EnumValue("4", "HC")]),
            Point("62002", "Text not found: id[], fw[CTC-01-01], lang[en-GB]", 0),
            Point("62193", "RPS", 35, ""),
            Point("62244", "Software version", 20230301),
        };

        var readings = HeatPumpReadings.ForLog(points);

        Assert.Equal(["62000", "62193"], readings.Select(r => r.ParameterId));
        Assert.Null(readings[1].Unit);
    }

    private static LoggedReading Outdoor(double value) => Reading("62000", "Outdoor Temperature", value, "°C");

    private static LoggedReading Reading(string id, string name, double value, string? unit = "°C") =>
        new(id, name, unit, "General", value);

    private static DataPoint Point(
        string id, string name, double value, string? unit = null, bool writable = false, IReadOnlyList<EnumValue>? labels = null) =>
        new("General", id, name, unit, writable, null, value, null, labels);
}
