using System.Net;
using ModelContextProtocol;
using Sev.Client;
using Sev.Client.Models;
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

    [Fact]
    public void ThrowIfEmptyAnswer_EmptyAnswerWithoutCachedData_MentionsRateLimit()
    {
        var result = new UsageResult([new MeterUsage("1111", [])], (new DateTime(2026, 11, 1), new DateTime(2026, 11, 2)), EmptyAnswer: true);

        var ex = Assert.Throws<McpException>(() => SevToolErrors.ThrowIfEmptyAnswer(result));

        Assert.Contains("rate limit", ex.Message);
    }

    [Fact]
    public void ThrowIfEmptyAnswer_EmptyAnswerWithCachedData_ReturnsIt()
    {
        var reading = new UsageReading(new DateTime(2026, 11, 1), 1.0m, null, "kWh");
        var result = new UsageResult([new MeterUsage("1111", [reading])], (new DateTime(2026, 11, 2), new DateTime(2026, 11, 3)), EmptyAnswer: true);

        Assert.Same(result, SevToolErrors.ThrowIfEmptyAnswer(result));
    }

    [Fact]
    public void ThrowIfEmptyAnswer_NothingToFetch_IsNotAnError()
    {
        var result = new UsageResult([], null);

        Assert.Same(result, SevToolErrors.ThrowIfEmptyAnswer(result));
    }

    [Fact]
    public async Task CallAsync_TurnsLocalRateLimitIntoMcpExceptionWithWaitTime()
    {
        var limitError = new SevRateLimitExceededException(TimeSpan.FromSeconds(89.2));

        var ex = await Assert.ThrowsAsync<McpException>(() => SevToolErrors.CallAsync<int>(() => throw limitError));

        Assert.Contains("10 calls per 5 minutes", ex.Message);
        Assert.Contains("90 seconds", ex.Message);
        Assert.Same(limitError, ex.InnerException);
    }
}
