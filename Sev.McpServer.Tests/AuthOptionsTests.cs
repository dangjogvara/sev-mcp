namespace Sev.McpServer.Tests;

public class AuthOptionsTests
{
    private static AuthOptions Valid() => new()
    {
        Authority = "https://login.microsoftonline.com/tenant-id/v2.0",
        Resource = "https://sev.example.com/mcp",
        Audiences = ["api-client-id"],
        Scopes = ["api://api-client-id/mcp.access"],
        AllowedUsers = ["11111111-1111-1111-1111-111111111111"],
    };

    [Fact]
    public void Enabled_OnlyWithAuthority()
    {
        Assert.False(new AuthOptions().Enabled);
        Assert.True(Valid().Enabled);
    }

    [Fact]
    public void Validate_CompleteConfiguration_HasNoProblems()
    {
        Assert.Empty(Valid().Validate());
    }

    [Theory]
    [InlineData("http://login.example.com/v2.0", null, "Auth:Authority")]
    [InlineData(null, "https://sev.example.com/mcp/", "Auth:Resource")]
    [InlineData(null, "http://sev.example.com/mcp", "Auth:Resource")]
    [InlineData(null, "/mcp", "Auth:Resource")]
    public void Validate_RejectsBadUrls(string? authority, string? resource, string expected)
    {
        var options = Valid();
        options.Authority = authority ?? options.Authority;
        options.Resource = resource ?? options.Resource;

        Assert.Contains(options.Validate(), p => p.StartsWith(expected));
    }

    [Fact]
    public void Validate_RequiresAudiencesScopesAndAllowedUsers()
    {
        var options = Valid();
        options.Audiences = [];
        options.Scopes = [];
        options.AllowedUsers = [];

        var problems = options.Validate();

        Assert.Contains(problems, p => p.StartsWith("Auth:Audiences"));
        Assert.Contains(problems, p => p.StartsWith("Auth:Scopes"));
        Assert.Contains(problems, p => p.StartsWith("Auth:AllowedUsers"));
    }

    [Fact]
    public void Validate_RejectsBlankArrayEntries()
    {
        var options = Valid();
        options.Audiences = [" "];
        options.Scopes = ["api://api-client-id/"];
        options.AllowedUsers = [""];

        var problems = options.Validate();

        Assert.Contains(problems, p => p.StartsWith("Auth:Audiences"));
        Assert.Contains(problems, p => p.StartsWith("Auth:Scopes"));
        Assert.Contains(problems, p => p.StartsWith("Auth:AllowedUsers"));
    }

    [Theory]
    [InlineData("api://api-client-id/mcp.access", "mcp.access")]
    [InlineData("  api://api-client-id/mcp.access  ", "mcp.access")]
    [InlineData("mcp.access", "mcp.access")]
    [InlineData("api://api-client-id/", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void ShortScopeName_ExtractsTrailingScopeSegment(string? input, string expected)
    {
        Assert.Equal(expected, AuthOptions.ShortScopeName(input));
    }
}
