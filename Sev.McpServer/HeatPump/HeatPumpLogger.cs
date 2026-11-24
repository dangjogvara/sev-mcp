using MyUplink.Client;
using Sev.Client;
using Sev.Core.HeatPump;

namespace Sev.McpServer.HeatPump;

// Logs the heat pump's readings once an hour into HeatPumpLog, because myUplink doesn't give its history to
// apps. It checks every CheckInterval and takes a sample when a device from myUplink's last answer has none
// for the current local hour yet, so a restart or a failed call (myUplink down, rate limit) is caught up
// within minutes, and a sample costs one call for the systems plus one per device not yet logged. A device that isn't connected to myUplink is skipped, since
// myUplink only has its last values, so those hours show as missing in the history. Only runs when myUplink is configured and MyUplink:LogHourly
// isn't false (tests turn it off so they never call myUplink).
public class HeatPumpLogger(IServiceScopeFactory scopes, HeatPumpLog log, TimeProvider time, ILogger<HeatPumpLogger> logger)
    : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    // The devices in myUplink's last answer; null until the first one. Only the logger's loop uses it.
    private IReadOnlyList<string>? _devices;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval, time);
        do
        {
            try
            {
                await SampleIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Tried again at the next check; the hour is only lost if it fails all hour.
                logger.LogWarning(ex, "Couldn't log the heat pump's readings this time.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task SampleIfDueAsync(CancellationToken cancellationToken)
    {
        var hour = SevTime.StartOfHour(SevTime.LocalNow(time));
        if (_devices is { Count: > 0 } known && known.All(id => log.HasSample(id, hour)))
        {
            return;
        }
        // MyUplinkClient is a typed HttpClient and short-lived, so it comes from a scope each time.
        using var scope = scopes.CreateScope();
        var myUplink = scope.ServiceProvider.GetRequiredService<MyUplinkClient>();
        var sample = await TakeSampleAsync(myUplink, log, hour, cancellationToken);
        _devices = sample.Devices;
        if (sample.Offline.Count > 0)
        {
            // The device has no sample for the hour, so the next check tries again.
            logger.LogWarning("The heat pump isn't connected to myUplink, so it isn't logged this time: {Devices}.",
                string.Join(", ", sample.Offline));
        }
        if (sample.Logged > 0)
        {
            logger.LogInformation("Logged the heat pump's readings for {Hour:yyyy-MM-dd HH:mm} ({Devices} devices).", hour, sample.Logged);
        }
    }

    // Reads the current values of every connected device that has no sample for the hour yet and logs the
    // measurements. Returns how many devices were logged, the names of those skipped because they weren't
    // connected, and the IDs of all devices myUplink listed.
    public static async Task<Sample> TakeSampleAsync(MyUplinkClient myUplink, HeatPumpLog log, DateTime hour, CancellationToken cancellationToken)
    {
        var logged = 0;
        var offline = new List<string>();
        var devices = new List<string>();
        foreach (var system in await myUplink.GetSystemsAsync(cancellationToken))
        {
            foreach (var device in system.Devices ?? [])
            {
                devices.Add(device.Id);
                if (log.HasSample(device.Id, hour))
                {
                    continue;
                }
                if (HeatPumpReadings.IsOffline(device))
                {
                    offline.Add(device.Product?.Name ?? device.Id);
                    continue;
                }
                var points = await myUplink.GetPointsAsync(device.Id, cancellationToken);
                var readings = HeatPumpReadings.ForLog(points);
                if (readings.Count > 0)
                {
                    log.Add(device.Id, device.Product?.Name, hour, readings);
                    logged++;
                }
            }
        }
        return new Sample(logged, offline, devices);
    }

    public record Sample(int Logged, IReadOnlyList<string> Offline, IReadOnlyList<string> Devices);
}
