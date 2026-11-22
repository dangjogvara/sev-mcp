namespace Sev.McpServer;

// The "Auth" configuration section. When Authority is set, /mcp requires an OAuth access token from that
// authorization server (Microsoft Entra ID), issued to one of AllowedUsers; see McpLogin. Without it the
// server only allows local use, and outside Development it refuses to start.
// Hosted, set it with environment variables, e.g. Auth__Authority, Auth__AllowedUsers__0.
public class AuthOptions
{
    public const string SectionName = "Auth";

    // The token issuer, e.g. https://login.microsoftonline.com/<tenant-id>/v2.0 (v2 tokens).
    // Claude finds it through the protected resource metadata and logs the user in there.
    public string? Authority { get; set; }

    // The public MCP URL, exactly as entered in Claude (https, no trailing slash), e.g. https://<app>.azurewebsites.net/mcp.
    // Claude requires the metadata's "resource" to match it.
    public string? Resource { get; set; }

    // Accepted "aud" values of access tokens. Entra v2 tokens carry the API app registration's client ID.
    public string[] Audiences { get; set; } = [];

    // Scopes Claude should ask for, advertised in the metadata, e.g. api://<client-id>/mcp.access.
    public string[] Scopes { get; set; } = [];

    // Entra object IDs ("oid" claim) of the users allowed in. Anyone else with a valid token gets 403.
    public string[] AllowedUsers { get; set; } = [];

    public bool Enabled => !string.IsNullOrWhiteSpace(Authority);

    // Entra metadata lists full URI scopes (api://<client-id>/mcp.access), while v2 access tokens
    // put the short scope name (mcp.access) in the "scp" claim.
    public static string ShortScopeName(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return "";
        }
        var trimmed = scope.Trim();
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    // What's wrong with an enabled configuration, in words; empty when it can be used.
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!IsHttpsUrl(Authority))
        {
            problems.Add("Auth:Authority must be an https URL, e.g. https://login.microsoftonline.com/<tenant-id>/v2.0.");
        }
        if (!IsHttpsUrl(Resource) || Resource!.EndsWith('/'))
        {
            problems.Add("Auth:Resource must be the public https MCP URL without a trailing slash, e.g. https://<app>.azurewebsites.net/mcp.");
        }
        if (Audiences.Length == 0 || Audiences.Any(string.IsNullOrWhiteSpace))
        {
            problems.Add("Auth:Audiences needs at least one accepted token audience (the API app registration's client ID).");
        }
        if (Scopes.Length == 0 || Scopes.Any(s => string.IsNullOrWhiteSpace(ShortScopeName(s))))
        {
            problems.Add("Auth:Scopes needs at least one scope (e.g. api://<client-id>/mcp.access).");
        }
        if (AllowedUsers.Length == 0 || AllowedUsers.Any(string.IsNullOrWhiteSpace))
        {
            problems.Add("Auth:AllowedUsers needs at least one Entra object ID; otherwise nobody could use the server.");
        }
        return problems;
    }

    private static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
