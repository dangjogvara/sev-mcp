using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sev.Client;
using Sev.McpServer.Tools;

namespace Sev.McpServer;

public static class SevServices
{
    // Everything the tools need to reach SEV: credentials, one shared login and rate limit, the cache,
    // and SevDataService on top. Tools only ever take SevDataService.
    public static IServiceCollection AddSevClient(this IServiceCollection services)
    {
        services
            .AddOptions<SevOptions>()
            .BindConfiguration(SevOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(o => MeterNicknames.Problems(o.MeterNicknames).Count == 0,
                "Sev:MeterNicknames: every key must be a meter ID (a number, as list_meters shows it) and every nickname non-empty.")
            .ValidateOnStart();
        services.AddSingleton(sp => MeterNicknames.From(sp.GetRequiredService<IOptions<SevOptions>>().Value.MeterNicknames));

        // The clock the tools read "now" from (tests can replace it).
        services.TryAddSingleton(TimeProvider.System);

        // One rate limiter for every SEV call (login included), shared by both HttpClients below.
        services.AddSingleton(new SevRateLimiter());
        services.AddTransient<SevRateLimitHandler>();

        // One token provider for the whole server, so all tool calls share one login.
        // It keeps its own long-lived HttpClient for login; PooledConnectionLifetime makes it pick up DNS changes.
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SevOptions>>().Value;
            var network = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) };
            var http = new HttpClient(new SevRateLimitHandler(sp.GetRequiredService<SevRateLimiter>()) { InnerHandler = network })
            {
                BaseAddress = SevClient.DefaultBaseAddress
            };
            return new SevTokenProvider(http, options.User, options.Key);
        });

        // Handlers run in the order added: SevAuthHandler first (may log in), then SevRateLimitHandler
        // right before the network, so only requests that are actually sent are counted.
        services.AddTransient<SevAuthHandler>();
        services
            .AddHttpClient<SevClient>(http => http.BaseAddress = SevClient.DefaultBaseAddress)
            .AddHttpMessageHandler<SevAuthHandler>()
            .AddHttpMessageHandler<SevRateLimitHandler>();

        // The cache outlives requests; the service that uses it is created per use, like the SevClient it wraps.
        // With Sev:CacheDirectory set, the cache is saved there and reloaded when the server restarts.
        services.AddSingleton(sp =>
        {
            var directory = sp.GetRequiredService<IOptions<SevOptions>>().Value.CacheDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                return new SevCache();
            }
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<SevCache>();
            return new SevCache(files: new SevCacheFiles(directory, message => logger.LogInformation("{Message}", message)));
        });
        services.AddTransient<SevDataService>();

        return services;
    }
}
