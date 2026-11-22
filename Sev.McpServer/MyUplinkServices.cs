using MyUplink.Client;

namespace Sev.McpServer;

// The optional "MyUplink" configuration section: the client ID and secret of an application registered at
// https://dev.myuplink.com. Locally in user-secrets ("MyUplink:ClientId"), hosted as MyUplink__ClientId and
// MyUplink__ClientSecret. Without it the server has no heat pump tools.
public class MyUplinkOptions
{
    public const string SectionName = "MyUplink";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public bool PartlyConfigured => !Enabled && (!string.IsNullOrWhiteSpace(ClientId) || !string.IsNullOrWhiteSpace(ClientSecret));
}

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
        return true;
    }
}
