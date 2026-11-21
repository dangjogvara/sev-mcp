using System.ComponentModel.DataAnnotations;

namespace Sev.McpServer;

// Bound from the "Sev" configuration section: user-secrets locally ("Sev:User", "Sev:Key"),
// environment variables when hosted (Sev__User, Sev__Key).
public class SevOptions
{
    public const string SectionName = "Sev";

    [Required]
    public string User { get; set; } = "";

    [Required]
    public string Key { get; set; } = "";
}
