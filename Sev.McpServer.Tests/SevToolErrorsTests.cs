using System.Net;
using ModelContextProtocol;
using Sev.Client;
using Sev.McpServer.Tools;

namespace Sev.McpServer.Tests;

public class SevToolErrorsTests
{
    [Fact]
    public async Task CallAsync_ReturnsResult()
    {
        Assert.Equal(42, await SevToolErrors.CallAsync(() => Task.FromResult(42)));
    }

    [Fact]
    public async Task CallAsync_TurnsSevApiExceptionIntoMcpException()
    {
        var sevError = new SevApiException(HttpStatusCode.BadRequest, "bad meter", null);

        var ex = await Assert.ThrowsAsync<McpException>(() => SevToolErrors.CallAsync<int>(() => throw sevError));

        Assert.Contains("bad meter", ex.Message);
        Assert.Same(sevError, ex.InnerException);
    }

    [Fact]
    public async Task CallAsync_LeavesOtherExceptionsAlone()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SevToolErrors.CallAsync<int>(() => throw new InvalidOperationException()));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void Describe_AuthFailure_PointsAtCredentials(HttpStatusCode status)
    {
        var message = SevToolErrors.Describe(new SevApiException(status, "", null));

        Assert.Contains("credentials", message);
    }

    [Fact]
    public void Describe_RateLimit_IncludesRetryAfter()
    {
        var message = SevToolErrors.Describe(
            new SevApiException(HttpStatusCode.TooManyRequests, "", TimeSpan.FromSeconds(89.5)));

        Assert.Contains("rate limit", message);
        Assert.Contains("90 seconds", message);
    }

    [Fact]
    public void Describe_RateLimit_WithoutRetryAfter()
    {
        var message = SevToolErrors.Describe(new SevApiException(HttpStatusCode.TooManyRequests, "", null));

        Assert.Contains("a few minutes", message);
    }

    [Fact]
    public void ThrowIfEmpty_ReturnsNonEmptyList()
    {
        int[] result = [1];

        Assert.Same(result, SevToolErrors.ThrowIfEmpty(result));
    }

    [Fact]
    public void ThrowIfEmpty_EmptyList_MentionsRateLimit()
    {
        var ex = Assert.Throws<McpException>(() => SevToolErrors.ThrowIfEmpty(Array.Empty<int>()));

        Assert.Contains("rate limit", ex.Message);
    }
}
