using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using Sev.Client;
using Sev.McpServer;
using Sev.McpServer.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<SevOptions>()
    .BindConfiguration(SevOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// One rate limiter for every SEV call (login included), shared by both HttpClients below.
builder.Services.AddSingleton(new SevRateLimiter());
builder.Services.AddTransient<SevRateLimitHandler>();

// One token provider for the whole server, so all tool calls share one login.
// It keeps its own long-lived HttpClient for login; PooledConnectionLifetime makes it pick up DNS changes.
builder.Services.AddSingleton(sp =>
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
builder.Services.AddTransient<SevAuthHandler>();
builder.Services
    .AddHttpClient<SevClient>(http => http.BaseAddress = SevClient.DefaultBaseAddress)
    .AddHttpMessageHandler<SevAuthHandler>()
    .AddHttpMessageHandler<SevRateLimitHandler>();

// The cache outlives requests; the service that uses it is created per use, like the SevClient it wraps.
builder.Services.AddSingleton(new SevCache());
builder.Services.AddTransient<SevDataService>();

// Keep æ, ø, á etc. as-is in tool results instead of \u00e6 escapes, which are harder to read and cost more tokens.
var toolJsonOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<MeterTools>(toolJsonOptions)
    .WithTools<UsageTools>(toolJsonOptions);

var app = builder.Build();

app.MapMcp("/mcp");

app.Run();
