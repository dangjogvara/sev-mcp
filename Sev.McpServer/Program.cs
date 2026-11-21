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

// Keep æ, ø, á etc. as-is in tool results instead of \u00e6 escapes, which are harder to read and cost more tokens.
var toolJsonOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<MeterTools>(toolJsonOptions);

var app = builder.Build();

app.MapMcp("/mcp");

app.Run();
