using MyUplink.Client;
using Sev.Client;
using Sev.Core.HeatPump;

namespace Sev.McpServer.HeatPump;

// Logs the heat pump's readings once an hour into HeatPumpLog, because myUplink doesn't give its history to
// apps. It checks every CheckInterval and takes a sample when the current local hour has none yet, so a
// restart or a failed call (myUplink down, rate limit) is caught up within minutes, and a sample costs one
// call for the systems plus one per device. Only runs when myUplink is configured and MyUplink:LogHourly
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
        var devices = await TakeSampleAsync(myUplink, log, hour, cancellationToken);
        logger.LogInformation("Logged the heat pump's readings for {Hour:yyyy-MM-dd HH:mm} ({Devices} devices).", hour, devices);
    }

    // Reads every device's current values and logs the measurements for the hour. Returns the number of devices.
    public static async Task<int> TakeSampleAsync(MyUplinkClient myUplink, HeatPumpLog log, DateTime hour, CancellationToken cancellationToken)
    {
        var devices = 0;
        foreach (var system in await myUplink.GetSystemsAsync(cancellationToken))
        {
            foreach (var device in system.Devices ?? [])
            {
                var points = await myUplink.GetPointsAsync(device.Id, cancellationToken);
                log.Add(device.Id, device.Product?.Name, hour, HeatPumpReadings.ForLog(points));
                devices++;
            }
        }
        return devices;
    }
}
