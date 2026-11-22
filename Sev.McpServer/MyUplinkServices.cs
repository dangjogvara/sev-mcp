using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyUplink.Client;
using Sev.McpServer.HeatPump;

namespace Sev.McpServer;

public static class MyUplinkServices
{
    // Adds the myUplink client when the MyUplink section is configured and says whether it did, so Program.cs
    // only registers the heat pump tools then. Users without a heat pump get exactly the SEV tools.
    public static bool AddMyUplinkIfConfigured(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(MyUplinkOptions.SectionName).Get<MyUplinkOptions>() ?? new MyUplinkOptions();
        if (!options.Enabled)
        {
            return options.PartlyConfigured
                ? throw new InvalidOperationException("MyUplink needs both ClientId and ClientSecret, or neither (no heat pump tools).")
                : false;
        }

        // One token provider for the whole server, with its own long-lived HttpClient for logging in;
        // PooledConnectionLifetime makes it pick up DNS changes.
        builder.Services.AddSingleton(_ =>
        {
            var network = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) };
            var http = new HttpClient(network) { BaseAddress = MyUplinkClient.DefaultBaseAddress };
            return new MyUplinkTokenProvider(http, options.ClientId!, options.ClientSecret!);
        });
        builder.Services.AddHttpClient<MyUplinkClient>(http => http.BaseAddress = MyUplinkClient.DefaultBaseAddress);

        // The hourly log, kept next to the SEV cache (Sev:CacheDirectory) so it survives restarts.
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(sp =>
        {
            var directory = sp.GetRequiredService<IOptions<SevOptions>>().Value.CacheDirectory;
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<HeatPumpLog>();
            return new HeatPumpLog(directory, message => logger.LogInformation("{Message}", message));
        });
        if (options.LogHourly)
        {
            builder.Services.AddHostedService<HeatPumpLogger>();
        }
        return true;
    }
}
