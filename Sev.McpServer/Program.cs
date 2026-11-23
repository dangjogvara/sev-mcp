using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol;
using Sev.Client;
using Sev.McpServer;
using Sev.McpServer.Tools;
using Sev.McpServer.Web;

var builder = WebApplication.CreateBuilder(args);

// Fail at startup rather than on the first tool call if the system has no time-zone database
// (SevTime.Zone needs Atlantic/Faroe; slim container images lack it until tzdata is installed).
_ = SevTime.Zone;

// Hosted: an OAuth login is required (the Auth section). Locally: same-machine use only. See McpLogin.
var auth = builder.AddLoginIfConfigured();

// SEV's API: credentials, shared login and rate limit, cache. See SevServices.
builder.Services.AddSevClient();

// Keep æ, ø, á etc. as-is in tool results instead of \u00e6 escapes, which are harder to read and cost more tokens.
var toolJsonOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

// Optional: heat pump status from myUplink, only when the MyUplink section is configured. See MyUplinkServices.
var heatPump = builder.AddMyUplinkIfConfigured();

var mcp = builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<MeterTools>(toolJsonOptions)
    .WithTools<UsageTools>(toolJsonOptions);
if (heatPump)
{
    mcp.WithTools<HeatPumpTools>(toolJsonOptions).WithTools<HeatPumpHistoryTools>(toolJsonOptions);
}

// Web pages (Blazor), local only until they have a login. See WebSetup.
builder.AddSevWeb();

var app = builder.Build();
app.MapMcpEndpoint(auth);
app.MapSevWeb(auth);

// For App Service's Always On (which pings "/") and Health check ("/health"): they answer 200 without a login,
// and without calling SEV or myUplink, so they never use up a rate limit.
app.MapGet("/health", () => "ok");
app.MapGet("/", () => "SEV MCP. Connect an MCP client to /mcp.");
app.Run();

// Lets the integration tests start the server with WebApplicationFactory<Program>.
public partial class Program;
