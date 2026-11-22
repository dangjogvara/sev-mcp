namespace Sev.McpServer.Tests;

public class AuthOptionsTests
{
    private static AuthOptions Valid() => new()
    {
        Authority = "https://login.microsoftonline.com/tenant-id/v2.0",
        Resource = "https://sev.example.com/mcp",
        Audiences = ["api-client-id"],
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
    public void Validate_RequiresAudiencesAndAllowedUsers()
    {
        var options = Valid();
        options.Audiences = [];
        options.AllowedUsers = [];

        var problems = options.Validate();

        Assert.Contains(problems, p => p.StartsWith("Auth:Audiences"));
        Assert.Contains(problems, p => p.StartsWith("Auth:AllowedUsers"));
    }
}
