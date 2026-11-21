namespace Sev.McpServer.Tests;

public class LocalOriginTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoOrigin_IsAllowed(string? origin)
    {
        // Claude Code and other non-browser clients don't send one.
        Assert.True(LocalOrigin.IsAllowed(origin));
    }

    [Theory]
    [InlineData("http://localhost:6274")]
    [InlineData("http://localhost")]
    [InlineData("https://localhost:7091")]
    [InlineData("http://127.0.0.1:5133")]
    [InlineData("http://[::1]:5133")]
    public void LocalhostOrigin_IsAllowed(string origin)
    {
        Assert.True(LocalOrigin.IsAllowed(origin));
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("http://127.0.0.1.nip.io")]
    [InlineData("http://192.168.1.10:5133")]
    [InlineData("null")]
    [InlineData("file:///home/user/page.html")]
    [InlineData("http://localhost:6274, https://evil.example")]
    public void OtherOrigin_IsRejected(string origin)
    {
        Assert.False(LocalOrigin.IsAllowed(origin));
    }
}
