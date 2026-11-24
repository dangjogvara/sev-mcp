using System.Globalization;
using System.Text.Json;

namespace Sev.Core.HeatPump;

// The heat pump's readings, one sample per hour, logged by HeatPumpLogger because myUplink doesn't give its
// history to apps (issue #96). Hours are local Faroese time (start of the hour), like SEV's readings, so the
// two line up. A singleton; every method takes the lock, so the logger and the tools can use it at once.
// With a directory (Sev:CacheDirectory) it's kept in one JSON file per month (HeatPumpLog-2026-11.json), so
// a save only rewrites the current month; without one it lives in memory and is lost on restart.
// Like SevCacheFiles: a file that can't be read is skipped and a failed save is only logged.
public class HeatPumpLog
{
    private const int FormatVersion = 1;
    private const string FilePrefix = "HeatPumpLog-";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, DeviceLog> _devices = [];
    private readonly string? _directory;
    private readonly Action<string>? _log;

    public HeatPumpLog(string? directory = null, Action<string>? log = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? null : directory;
        _log = log;
        Load();
    }

    // Whether the device has a sample for the hour. Per device, so a device that failed while another one was
    // logged is still tried again within the hour.
    public bool HasSample(string deviceId, DateTime hour)
    {
        lock (_lock)
        {
            return _devices.TryGetValue(deviceId, out var device) && device.Samples.ContainsKey(hour);
        }
    }

    // Records one hour's readings for a device and saves that month. An hour already logged is kept as it is,
    // so the repeated hour when summer time ends is logged once.
    public void Add(string deviceId, string? product, DateTime hour, IReadOnlyList<LoggedReading> readings)
    {
        if (readings.Count == 0)
        {
            return;
        }
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceId, out var device))
            {
                _devices[deviceId] = device = new DeviceLog(deviceId);
            }
            device.Product = product ?? device.Product;
            if (device.Samples.ContainsKey(hour))
            {
                return;
            }
            // A parameter that occurs twice in one answer keeps its last value rather than losing the sample.
            var values = new Dictionary<string, double>();
            foreach (var reading in readings)
            {
                device.Parameters[reading.ParameterId] = new LoggedParameter(reading.ParameterId, reading.Name, reading.Unit, reading.Category);
                values[reading.ParameterId] = reading.Value;
            }
            device.Samples[hour] = values;
            SaveMonth(hour.Year, hour.Month);
        }
    }

    // A copy of what's logged, for the tools to read without holding the lock.
    public IReadOnlyList<LoggedDevice> Snapshot()
    {
        lock (_lock)
        {
            return _devices.Values
                .Select(d => new LoggedDevice(
                    d.DeviceId,
                    d.Product,
                    d.Parameters.Values.ToList(),
                    d.Samples.Select(s => new LoggedSample(s.Key, new Dictionary<string, double>(s.Value))).ToList()))
                .ToList();
        }
    }

    private void Load()
    {
        if (_directory is null || !Directory.Exists(_directory))
        {
            return;
        }
        var samples = 0;
        foreach (var path in Directory.EnumerateFiles(_directory, FilePrefix + "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var file = JsonSerializer.Deserialize<MonthFile>(stream, FileJson);
                if (file?.Version != FormatVersion)
                {
                    _log?.Invoke($"Ignoring {path}: not format version {FormatVersion}.");
                    continue;
                }
                if (file.Devices is null)
                {
                    _log?.Invoke($"Ignoring {path}: missing devices.");
                    continue;
                }
                foreach (var d in file.Devices)
                {
                    if (string.IsNullOrEmpty(d?.DeviceId))
                    {
                        continue;
                    }
                    if (!_devices.TryGetValue(d.DeviceId, out var device))
                    {
                        _devices[d.DeviceId] = device = new DeviceLog(d.DeviceId);
                    }
                    device.Product = d.Product ?? device.Product;
                    foreach (var parameter in d.Parameters ?? [])
                    {
                        if (!string.IsNullOrEmpty(parameter?.Id))
                        {
                            device.Parameters[parameter.Id] = parameter;
                        }
                    }
                    foreach (var sample in d.Samples ?? [])
                    {
                        if (sample?.Values is not null)
                        {
                            device.Samples[sample.Hour] = new Dictionary<string, double>(sample.Values);
                            samples++;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _log?.Invoke($"Ignoring {path}: {ex.Message}");
            }
        }
        if (samples > 0)
        {
            _log?.Invoke($"Loaded {samples} logged heat pump hours from {_directory}.");
        }
    }

    // Writes one month's samples (all devices) to its file, via a temporary file and a rename.
    private void SaveMonth(int year, int month)
    {
        if (_directory is null)
        {
            return;
        }
        var devices = _devices.Values
            .Select(d => new DeviceMonth(
                d.DeviceId,
                d.Product,
                d.Parameters.Values.ToList(),
                d.Samples.Where(s => s.Key.Year == year && s.Key.Month == month)
                    .Select(s => new LoggedSample(s.Key, s.Value))
                    .ToList()))
            .Where(d => d.Samples.Count > 0)
            .ToList();
        var path = Path.Combine(_directory, $"{FilePrefix}{year:D4}-{month:D2}.json");
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, new MonthFile(FormatVersion, devices), FileJson);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Couldn't save {path}, keeping the heat pump log in memory only: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new LocalHourConverter() },
    };

    private sealed class DeviceLog(string deviceId)
    {
        public string DeviceId { get; } = deviceId;
        public string? Product { get; set; }
        public Dictionary<string, LoggedParameter> Parameters { get; } = [];
        public SortedDictionary<DateTime, Dictionary<string, double>> Samples { get; } = [];
    }

    private sealed record MonthFile(int Version, IReadOnlyList<DeviceMonth> Devices);

    private sealed record DeviceMonth(string DeviceId, string? Product, IReadOnlyList<LoggedParameter> Parameters, IReadOnlyList<LoggedSample> Samples);

    // Hours are local wall-clock times without an offset ("2026-11-22T18:00"), read back with Kind Unspecified.
    // A value that isn't one throws JsonException, so Load skips the file instead of failing the server's start.
    private sealed class LocalHourConverter : System.Text.Json.Serialization.JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
            && DateTime.TryParseExact(reader.GetString(), "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var hour)
                ? hour
                : throw new JsonException("An hour must look like 2026-11-22T18:00.");

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture));
    }
}

// One value read from the heat pump at the hour.
public record LoggedReading(string ParameterId, string Name, string? Unit, string? Category, double Value);

// What a logged parameter is, by its myUplink ID.
public record LoggedParameter(string Id, string Name, string? Unit, string? Category);

public record LoggedSample(DateTime Hour, IReadOnlyDictionary<string, double> Values);

public record LoggedDevice(string DeviceId, string? Product, IReadOnlyList<LoggedParameter> Parameters, IReadOnlyList<LoggedSample> Samples);
