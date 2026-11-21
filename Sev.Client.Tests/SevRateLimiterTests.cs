namespace Sev.Client.Tests;

public class SevRateLimiterTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly SevRateLimiter _limiter;

    public SevRateLimiterTests()
    {
        _limiter = new SevRateLimiter(_time);
    }

    [Fact]
    public void AllowsTenCalls_RefusesEleventh()
    {
        for (var i = 0; i < 10; i++)
        {
            Assert.True(_limiter.TryAcquire(out _));
        }

        Assert.False(_limiter.TryAcquire(out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(5), retryAfter);
    }

    [Fact]
    public void AllowsAgainOnceOldestCallIsFiveMinutesOld()
    {
        AcquireMany(10);

        _time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.False(_limiter.TryAcquire(out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(1), retryAfter);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_limiter.TryAcquire(out _));
    }

    [Fact]
    public void WindowSlides_OnlyExpiredCallsAreFreed()
    {
        AcquireMany(4);
        _time.Advance(TimeSpan.FromMinutes(2));
        AcquireMany(6);

        // At 5 minutes the first 4 calls expire; the 6 from minute 2 still count.
        _time.Advance(TimeSpan.FromMinutes(3));
        AcquireMany(4);
        Assert.False(_limiter.TryAcquire(out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(2), retryAfter);
    }

    [Fact]
    public void RefusedCallsAreNotCounted()
    {
        AcquireMany(10);
        for (var i = 0; i < 5; i++)
        {
            _limiter.TryAcquire(out _);
        }

        _time.Advance(TimeSpan.FromMinutes(5));

        AcquireMany(10);
    }

    [Fact]
    public void ConcurrentCallers_NeverExceedLimit()
    {
        var allowed = 0;
        Parallel.For(0, 100, i =>
        {
            if (_limiter.TryAcquire(out _))
            {
                Interlocked.Increment(ref allowed);
            }
        });

        Assert.Equal(10, allowed);
    }

    [Fact]
    public void CustomLimitAndWindow()
    {
        var limiter = new SevRateLimiter(_time, maxCalls: 2, window: TimeSpan.FromSeconds(30));

        Assert.True(limiter.TryAcquire(out _));
        Assert.True(limiter.TryAcquire(out _));
        Assert.False(limiter.TryAcquire(out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(30), retryAfter);
    }

    private void AcquireMany(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.True(_limiter.TryAcquire(out _), $"call {i + 1} of {count} was refused");
        }
    }
}
