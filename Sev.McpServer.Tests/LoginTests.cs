using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Sev.McpServer.Tests;

// Starts the real server in memory (no network, no SEV calls: initialize doesn't touch SEV) and checks the
// login: the 401 challenge and metadata Claude relies on, and which tokens get in. Tokens are signed with a
// test key instead of coming from Entra ID.
public class LoginTests
{
    private const string Issuer = "https://login.example.com/tenant-id/v2.0";
    private const string Resource = "https://sev.example.com/mcp";
    private const string Audience = "api-client-id";
    private const string Scope = "api://api-client-id/mcp.access";
    private const string AllowedUser = "11111111-1111-1111-1111-111111111111";

    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("test-signing-key-that-is-at-least-256-bits-long"));

    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";

    [Fact]
    public async Task NoToken_Gets401PointingAtTheMetadata()
    {
        using var factory = Server(withAuth: true);
        var response = await factory.CreateClient().SendAsync(InitializeRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            "Bearer resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp\"",
            Assert.Single(response.Headers.WwwAuthenticate).ToString());
    }

    [Fact]
    public async Task BehindHttpsProxy_ChallengeUsesHttps()
    {
        using var factory = Server(withAuth: true);
        var request = InitializeRequest();
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("https://localhost/.well-known/oauth-protected-resource/mcp", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Metadata_NamesTheResourceAuthorizationServerAndScopes()
    {
        using var factory = Server(withAuth: true);

        var response = await factory.CreateClient().GetAsync("/.well-known/oauth-protected-resource/mcp");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(Resource, json.RootElement.GetProperty("resource").GetString());
        Assert.Equal([Issuer], json.RootElement.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal([Scope, "offline_access"], json.RootElement.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task AllowedUser_CanUseMcp()
    {
        using var factory = Server(withAuth: true);

        var response = await factory.CreateClient().SendAsync(InitializeRequest(Token(AllowedUser)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("mcp.access")]
    [InlineData(null)]
    public async Task OtherUserWithValidToken_IsForbidden(string? scope)
    {
        using var factory = Server(withAuth: true);

        var response = await factory.CreateClient().SendAsync(InitializeRequest(Token("22222222-2222-2222-2222-222222222222", scope: scope)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("other.scope")]
    [InlineData("mcp.access.extra")]
    // Offered for the refresh token, but not enough to get in on its own.
    [InlineData("offline_access")]
    public async Task TokenWithoutRequiredScope_IsForbidden(string? scope)
    {
        using var factory = Server(withAuth: true);

        var response = await factory.CreateClient().SendAsync(InitializeRequest(Token(AllowedUser, scope: scope)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            $"Bearer error=\"insufficient_scope\", scope=\"{Scope}\", resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp\"",
            Assert.Single(response.Headers.WwwAuthenticate).ToString());
    }

    [Fact]
    public async Task TokenWithoutRequiredScope_BehindHttpsProxy_UsesHttpsMetadata()
    {
        using var factory = Server(withAuth: true);
        var request = InitializeRequest(Token(AllowedUser, scope: null));
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            $"Bearer error=\"insufficient_scope\", scope=\"{Scope}\", resource_metadata=\"https://localhost/.well-known/oauth-protected-resource/mcp\"",
            Assert.Single(response.Headers.WwwAuthenticate).ToString());
    }

    [Fact]
    public async Task TokenWithoutRequiredScope_ListsAllConfiguredScopes()
    {
        const string extraScope = "api://api-client-id/mcp.extra";
        using var factory = Server(withAuth: true, scopes: [Scope, extraScope]);

        var response = await factory.CreateClient().SendAsync(InitializeRequest(Token(AllowedUser, scope: null)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            $"Bearer error=\"insufficient_scope\", scope=\"{Scope} {extraScope}\", resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp\"",
            Assert.Single(response.Headers.WwwAuthenticate).ToString());
    }

    [Fact]
    public async Task TokenWithMultipleScopesIncludingRequired_CanUseMcp()
    {
        using var factory = Server(withAuth: true);

        var response = await factory.CreateClient().SendAsync(InitializeRequest(Token(AllowedUser, scope: "openid mcp.access profile")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("wrong-audience", Issuer)]
    [InlineData(Audience, "https://login.example.com/other-tenant/v2.0")]
    public async Task TokenForAnotherApiOrIssuer_IsRejected(string audience, string issuer)
    {
        using var factory = Server(withAuth: true);

        var response = await factory.CreateClient().SendAsync(InitializeRequest(Token(AllowedUser, audience, issuer)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredToken_IsRejected()
    {
        using var factory = Server(withAuth: true);

        var response = await factory.CreateClient().SendAsync(InitializeRequest(Token(AllowedUser, expires: DateTime.UtcNow.AddMinutes(-10))));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WithoutAuthInDevelopment_LocalUseWorksWithoutToken()
    {
        using var factory = Server(withAuth: false);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(InitializeRequest())).StatusCode);

        var crossSite = InitializeRequest();
        crossSite.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(crossSite)).StatusCode);
    }

    [Theory]
    [InlineData(true, "/health", "ok")]
    [InlineData(true, "/", "SEV MCP")]
    [InlineData(false, "/health", "ok")]
    public async Task HealthAndRoot_AnswerWithoutToken(bool withAuth, string path, string expected)
    {
        using var factory = Server(withAuth);

        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void WithoutAuthOutsideDevelopment_RefusesToStart()
    {
        using var factory = Server(withAuth: false, environment: "Production");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("requires a login", Messages(ex));
    }

    [Fact]
    public void IncompleteAuth_RefusesToStart()
    {
        using var factory = Server(withAuth: true, allowedUser: null);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Auth:AllowedUsers", Messages(ex));
    }

    private static WebApplicationFactory<Program> Server(
        bool withAuth, string environment = "Development", string? allowedUser = AllowedUser, string[]? scopes = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Sev:User", "test-user");
            builder.UseSetting("Sev:Key", "test-key");
            if (!withAuth)
            {
                return;
            }

            builder.UseSetting("Auth:Authority", Issuer);
            builder.UseSetting("Auth:Resource", Resource);
            builder.UseSetting("Auth:Audiences:0", Audience);
            var scopeList = scopes ?? [Scope];
            for (var i = 0; i < scopeList.Length; i++)
            {
                builder.UseSetting($"Auth:Scopes:{i}", scopeList[i]);
            }
            if (allowedUser is not null)
            {
                builder.UseSetting("Auth:AllowedUsers:0", allowedUser);
            }

            // Check tokens against the test key instead of downloading Entra's signing keys.
            builder.ConfigureTestServices(services =>
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    configuration.SigningKeys.Add(Key);
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                }));
        });

    private static string Token(
        string oid, string audience = Audience, string issuer = Issuer, DateTime? expires = null, string? scope = "mcp.access")
    {
        var expiry = expires ?? DateTime.UtcNow.AddHours(1);
        var claims = new Dictionary<string, object> { ["oid"] = oid };
        if (scope is not null)
        {
            claims["scp"] = scope;
        }
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = expiry.AddHours(-2),
            Expires = expiry,
            Claims = claims,
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        });
    }

    private static HttpRequestMessage InitializeRequest(string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(Initialize, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return request;
    }

    // The startup error can be wrapped; collect every message in the chain.
    private static string Messages(Exception ex)
    {
        var messages = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            messages.Add(e.Message);
        }
        return string.Join(" | ", messages);
    }
}
