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

    // Where the cache is saved so it survives restarts (Sev__CacheDirectory). Hosted on App Service this is
    // /home/data/sev-cache: /home persists, while /home/site/wwwroot is replaced by every deploy.
    // Empty (the default, and normal locally) keeps the cache in memory only.
    public string? CacheDirectory { get; set; }
}
