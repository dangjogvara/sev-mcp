using Sev.McpServer.Tools;

namespace Sev.McpServer.Tests;

public class FaroeseTimeTests
{
    [Theory]
    // No offset: already Faroese local time, used as-is.
    [InlineData("2026-10-10", "2026-10-10T00:00:00")]
    [InlineData("2026-10-10T06:00", "2026-10-10T06:00:00")]
    [InlineData("2026-10-10T06:00:00", "2026-10-10T06:00:00")]
    [InlineData("2026-10-10 06:00", "2026-10-10T06:00:00")]
    [InlineData(" 2026-10-10 ", "2026-10-10T00:00:00")]
    // Winter (WET, UTC+0): UTC equals Faroese time.
    [InlineData("2026-01-15T12:00:00Z", "2026-01-15T12:00:00")]
    // Summer (WEST, UTC+1): Faroese time is one hour ahead of UTC.
    [InlineData("2026-07-15T12:00:00Z", "2026-07-15T13:00:00")]
    [InlineData("2026-07-15T23:30:00Z", "2026-07-16T00:30:00")]
    // Other offsets.
    [InlineData("2026-07-15T14:00:00+02:00", "2026-07-15T13:00:00")]
    [InlineData("2026-01-15T07:00:00-05:00", "2026-01-15T12:00:00")]
    [InlineData("2026-01-15T12:00Z", "2026-01-15T12:00:00")]
    [InlineData("2026-07-15T12:00:00.5+00:00", "2026-07-15T13:00:00.5")]
    public void TryParse_ReturnsFaroeseLocalTime(string input, string expected)
    {
        Assert.True(FaroeseTime.TryParse(input, out var local));

        Assert.Equal(DateTime.Parse(expected), local);
        Assert.Equal(DateTimeKind.Unspecified, local.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("2026-13-01")]
    // Not ISO: these would be read for the wrong period (today on the server's clock, or the American way).
    [InlineData("06:00")]
    [InlineData("11/10/2026")]
    [InlineData("10.11.2026")]
    [InlineData("2026-1-5")]
    [InlineData("November 10, 2026")]
    public void TryParse_RejectsUnreadableInput(string? input)
    {
        Assert.False(FaroeseTime.TryParse(input, out _));
    }
}
