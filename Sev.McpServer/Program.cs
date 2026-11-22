using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using Sev.Client;
using Sev.McpServer;
using Sev.McpServer.Tools;

var builder = WebApplication.CreateBuilder(args);

// Login: required when the Auth section is configured (hosted), and the server refuses to run unprotected
// outside Development. Locally, without it, only same-machine use is allowed (see below).
var auth = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
if (auth.Enabled)
{
    var problems = auth.Validate();
    if (problems.Count > 0)
    {
        throw new InvalidOperationException("Invalid Auth configuration: " + string.Join(" ", problems));
    }
    builder.Services.AddMcpLogin(auth);
}
else if (!builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "No Auth configuration: outside Development the server requires a login. Set Auth:Authority, Auth:Resource, " +
        "Auth:Audiences and Auth:AllowedUsers (see README).");
}

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

if (auth.Enabled)
{
    app.UseForwardedHeaders();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapMcp("/mcp").RequireAuthorization(McpLogin.AllowedUsersPolicy);
}
else
{
    // Against DNS rebinding: a web page in the user's browser must not reach this unauthenticated server.
    // AllowedHosts (appsettings.json) rejects foreign Host headers; this rejects cross-site Origin headers.
    app.Use(async (context, next) =>
    {
        if (!LocalOrigin.IsAllowed(context.Request.Headers.Origin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    });
    app.MapMcp("/mcp");
}

app.Run();

// Lets the integration tests start the server with WebApplicationFactory<Program>.
public partial class Program;
