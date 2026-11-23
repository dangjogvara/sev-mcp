using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;

namespace Sev.McpServer;

// OAuth for the hosted server, as Claude's custom connectors expect it (MCP authorization spec):
// - Without a valid token, /mcp answers 401 with WWW-Authenticate: Bearer resource_metadata="...".
// - That metadata (RFC 9728, served by the SDK's McpAuth scheme at /.well-known/oauth-protected-resource/mcp)
//   names the MCP URL and the authorization server, where Claude logs the user in.
// - Requests then carry an access token, which JwtBearer checks (signature, issuer, audience, expiry). Only
//   users in AuthOptions.AllowedUsers whose token holds one of AuthOptions.Scopes ("scp" claim) get through.
public static class McpLogin
{
    public const string AllowedUsersPolicy = "AllowedUsers";

    // The OpenID Connect scope that makes the authorization server issue a refresh token.
    public const string OfflineAccess = "offline_access";

    // Reads the Auth section and adds the login when it's configured (hosted). Without it the server only
    // allows local use, and outside Development it refuses to start, so it can't go online unprotected.
    public static AuthOptions AddLoginIfConfigured(this WebApplicationBuilder builder)
    {
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
                "Auth:Audiences, Auth:Scopes and Auth:AllowedUsers (see README).");
        }
        return auth;
    }

    // Maps /mcp behind the login when it's configured, or behind the local-only checks when it isn't.
    public static void MapMcpEndpoint(this WebApplication app, AuthOptions auth)
    {
        if (auth.Enabled)
        {
            app.UseForwardedHeaders();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapMcp("/mcp").RequireAuthorization(AllowedUsersPolicy);
        }
        else
        {
            app.UseLocalOriginsOnly();
            app.MapMcp("/mcp");
        }
    }

    private static void AddMcpLogin(this IServiceCollection services, AuthOptions auth)
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
                // Entra only issues a refresh token when offline_access is requested. Without it Claude's access
                // runs out with the access token (60-90 minutes) and the user has to reconnect. It isn't checked
                // in tokens: the policy below still requires one of Auth:Scopes.
                if (!metadata.ScopesSupported.Contains(OfflineAccess))
                {
                    metadata.ScopesSupported.Add(OfflineAccess);
                }
                options.ResourceMetadata = metadata;
            });

        var requiredScopes = auth.Scopes.Select(AuthOptions.ShortScopeName).ToHashSet(StringComparer.Ordinal);
        services.AddAuthorization(options => options.AddPolicy(AllowedUsersPolicy, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim("oid", auth.AllowedUsers)
            .RequireAssertion(context => HasRequiredScope(context.User, requiredScopes))));

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

    // Entra sends granted delegated scopes as a space-delimited "scp" claim.
    private static bool HasRequiredScope(ClaimsPrincipal user, HashSet<string> requiredScopes) =>
        user.FindAll("scp")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(requiredScopes.Contains);
}
