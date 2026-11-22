namespace Sev.McpServer;

// The optional "MyUplink" configuration section: the client ID and secret of an application registered at
// https://dev.myuplink.com. Locally in user-secrets ("MyUplink:ClientId"), hosted as MyUplink__ClientId and
// MyUplink__ClientSecret. Without it the server has no heat pump tools.
public class MyUplinkOptions
{
    public const string SectionName = "MyUplink";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    // Log the heat pump's readings every hour (HeatPumpLogger), since myUplink gives apps no history.
    // On by default; tests turn it off so they never call myUplink.
    public bool LogHourly { get; set; } = true;

    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public bool PartlyConfigured => !Enabled && (!string.IsNullOrWhiteSpace(ClientId) || !string.IsNullOrWhiteSpace(ClientSecret));
}
