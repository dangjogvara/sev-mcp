using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;

namespace Sev.McpServer;

// OAuth for the hosted server, as Claude's custom connectors expect it (MCP authorization spec):
// - Without a valid token, /mcp answers 401 with WWW-Authenticate: Bearer resource_metadata="...".
// - That metadata (RFC 9728, served by the SDK's McpAuth scheme at /.well-known/oauth-protected-resource/mcp)
//   names the MCP URL and the authorization server, where Claude logs the user in.
// - Requests then carry an access token, which JwtBearer checks (signature, issuer, audience, expiry), and
//   only users in AuthOptions.AllowedUsers get through.
public static class McpLogin
{
    public const string AllowedUsersPolicy = "AllowedUsers";

    public static void AddMcpLogin(this IServiceCollection services, AuthOptions auth)
    {
        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.Authority = auth.Authority;
                // Keep Entra's claim names ("oid", "scp") instead of .NET's long URIs.
                options.MapInboundClaims = false;
                options.TokenValidationParameters.ValidAudiences = auth.Audiences;
            })
            .AddMcp(options =>
            {
                var metadata = new ProtectedResourceMetadata
                {
                    Resource = auth.Resource,
                    ResourceName = "SEV MCP",
                };
                metadata.AuthorizationServers.Add(auth.Authority!);
                foreach (var scope in auth.Scopes)
                {
                    metadata.ScopesSupported.Add(scope);
                }
                options.ResourceMetadata = metadata;
            });

        services.AddAuthorization(options => options.AddPolicy(AllowedUsersPolicy, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim("oid", auth.AllowedUsers)));

        // App Service ends HTTPS at its front end and forwards plain HTTP with X-Forwarded-Proto: https.
        // Honouring it makes the metadata URL in the 401 challenge start with https, as Claude requires.
        // Only the scheme is taken over (not the host), and the front end's addresses aren't fixed, so any
        // proxy is trusted for it.
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
    }
}
