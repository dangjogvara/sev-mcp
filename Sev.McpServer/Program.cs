using Microsoft.Extensions.Options;
using Sev.Client;
using Sev.McpServer;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<SevOptions>()
    .BindConfiguration(SevOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// One token provider for the whole server, so all tool calls share one login.
// It keeps its own long-lived HttpClient for login; PooledConnectionLifetime makes it pick up DNS changes.
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<SevOptions>>().Value;
    var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) })
    {
        BaseAddress = SevClient.DefaultBaseAddress
    };
    return new SevTokenProvider(http, options.User, options.Key);
});

builder.Services.AddTransient<SevAuthHandler>();
builder.Services
    .AddHttpClient<SevClient>(http => http.BaseAddress = SevClient.DefaultBaseAddress)
    .AddHttpMessageHandler<SevAuthHandler>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport();

var app = builder.Build();

app.MapMcp("/mcp");

app.Run();
