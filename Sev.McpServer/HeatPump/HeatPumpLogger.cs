using MyUplink.Client;
using Sev.Client;
using Sev.Core.HeatPump;

namespace Sev.McpServer.HeatPump;

// Logs the heat pump's readings once an hour into HeatPumpLog, because myUplink doesn't give its history to
// apps. It checks every CheckInterval and takes a sample when the current local hour has none yet, so a
// restart or a failed call (myUplink down, rate limit) is caught up within minutes, and a sample costs one
// call for the systems plus one per device. A device that isn't connected to myUplink is skipped, since
// myUplink only has its last values, so those hours show as missing in the history. Only runs when myUplink is configured and MyUplink:LogHourly
// isn't false (tests turn it off so they never call myUplink).
public class HeatPumpLogger(IServiceScopeFactory scopes, HeatPumpLog log, TimeProvider time, ILogger<HeatPumpLogger> logger)
    : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

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

    private async Task SampleIfDueAsync(CancellationToken cancellationToken)
    {
        var hour = SevTime.StartOfHour(SevTime.LocalNow(time));
        if (log.HasSample(hour))
        {
            return;
        }
        // MyUplinkClient is a typed HttpClient and short-lived, so it comes from a scope each time.
        using var scope = scopes.CreateScope();
        var myUplink = scope.ServiceProvider.GetRequiredService<MyUplinkClient>();
        var sample = await TakeSampleAsync(myUplink, log, hour, cancellationToken);
        if (sample.Offline.Count > 0)
        {
            // Without any device logged the hour has no sample, so the next check tries again.
            logger.LogWarning("The heat pump isn't connected to myUplink, so it isn't logged this time: {Devices}.",
                string.Join(", ", sample.Offline));
        }
        if (sample.Logged > 0)
        {
            logger.LogInformation("Logged the heat pump's readings for {Hour:yyyy-MM-dd HH:mm} ({Devices} devices).", hour, sample.Logged);
        }
    }

    // Reads every connected device's current values and logs the measurements for the hour. Returns how many
    // devices were logged and the names of those skipped because they weren't connected.
    public static async Task<Sample> TakeSampleAsync(MyUplinkClient myUplink, HeatPumpLog log, DateTime hour, CancellationToken cancellationToken)
    {
        var logged = 0;
        var offline = new List<string>();
        foreach (var system in await myUplink.GetSystemsAsync(cancellationToken))
        {
            foreach (var device in system.Devices ?? [])
            {
                if (HeatPumpReadings.IsOffline(device))
                {
                    offline.Add(device.Product?.Name ?? device.Id);
                    continue;
                }
                var points = await myUplink.GetPointsAsync(device.Id, cancellationToken);
                log.Add(device.Id, device.Product?.Name, hour, HeatPumpReadings.ForLog(points));
                logged++;
            }
        }
        return new Sample(logged, offline);
    }

    public record Sample(int Logged, IReadOnlyList<string> Offline);
}
