using System.Text.Json;
using Sev.Client.Models;

namespace Sev.Client;

// Keeps what SevCache holds in JSON files in one directory, so a restart (a deploy, a settings change,
// App Service maintenance) doesn't throw away the cached days and cost rate-limited calls to fetch them again.
// - One file per hourly series, named after its reading type (UsageReading.json, ...), plus Meters.json.
// - Only what SevCache keeps is saved: finished days, which never change, and the meter list with the
//   time it was fetched, so its 6-hour lifetime carries on after a restart.
// - Each file is written to a temporary file and then renamed over the old one, so a crash mid-write
//   leaves the previous version. No file locking is needed: SevDataService saves while holding the cache lock.
// - A file that is missing, unreadable or from another format version is skipped (the cache starts
//   without it), and a failed save leaves the data in memory: the files only ever save calls, so a
//   problem with them must never break a tool call.
// JSON rather than SQLite: on App Service /home is a network share, where SQLite's locking isn't reliable.
public class SevCacheFiles(string directory, Action<string>? log = null)
{
    // Bump when the file layout changes; files with another version are ignored and rewritten.
    private const int FormatVersion = 1;

    private const string MetersFileName = "Meters.json";

    public string Directory { get; } = directory;

    // Fills the cache with whatever can be read. Called once, when the cache is created.
    internal void LoadInto(SevCache cache)
    {
        if (Read<MetersFile>(MetersFileName) is { Customers.Count: > 0 } meters)
        {
            cache.Meters = meters.Customers;
            cache.MetersFetchedAt = meters.FetchedAt;
        }
        LoadDays(cache.UsageDays);
        LoadDays(cache.CostDays);
        LoadDays(cache.Co2Days);
    }

    internal void SaveMeters(IReadOnlyList<Customer> customers, DateTimeOffset fetchedAt) =>
        Write(MetersFileName, new MetersFile(FormatVersion, fetchedAt, customers));

    internal void SaveDays<TReading>(Dictionary<(int MeterId, DateOnly Day), IReadOnlyList<TReading>> days)
    {
        var entries = days
            .OrderBy(d => d.Key.MeterId).ThenBy(d => d.Key.Day)
            .Select(d => new DayEntry<TReading>(d.Key.MeterId, d.Key.Day, d.Value))
            .ToList();
        Write(DaysFileName<TReading>(), new DaysFile<TReading>(FormatVersion, entries));
    }

    private void LoadDays<TReading>(Dictionary<(int MeterId, DateOnly Day), IReadOnlyList<TReading>> days)
    {
        var fileName = DaysFileName<TReading>();
        var file = Read<DaysFile<TReading>>(fileName);
        if (file is null)
        {
            return;
        }
        if (file.Days is null)
        {
            log?.Invoke($"Ignoring {Path.Combine(Directory, fileName)}: missing days.");
            return;
        }
        var loaded = 0;
        foreach (var entry in file.Days)
        {
            if (entry?.Readings is null)
            {
                continue;
            }
            days[(entry.MeterId, entry.Day)] = entry.Readings;
            loaded++;
        }
        log?.Invoke($"Loaded {loaded} cached days from {fileName}.");
    }

    private static string DaysFileName<TReading>() => $"{typeof(TReading).Name}.json";

    // Null when the file doesn't exist, can't be read or parsed, or has another format version.
    private T? Read<T>(string fileName) where T : class, IVersioned
    {
        var path = Path.Combine(Directory, fileName);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var stream = File.OpenRead(path);
            var file = JsonSerializer.Deserialize<T>(stream, SevClient.JsonOptions);
            if (file?.Version == FormatVersion)
            {
                return file;
            }
            log?.Invoke($"Ignoring {path}: not format version {FormatVersion}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log?.Invoke($"Ignoring {path}: {ex.Message}");
        }
        return null;
    }

    private void Write<T>(string fileName, T content)
    {
        var path = Path.Combine(Directory, fileName);
        var temporary = path + ".tmp";
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, content, SevClient.JsonOptions);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Couldn't save {path}, keeping the data in memory only: {ex.Message}");
        }
    }

    private interface IVersioned
    {
        int Version { get; }
    }

    private sealed record MetersFile(int Version, DateTimeOffset FetchedAt, IReadOnlyList<Customer> Customers) : IVersioned;

    private sealed record DaysFile<TReading>(int Version, IReadOnlyList<DayEntry<TReading>> Days) : IVersioned;

    private sealed record DayEntry<TReading>(int MeterId, DateOnly Day, IReadOnlyList<TReading> Readings);
}
