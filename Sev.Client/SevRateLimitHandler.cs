namespace Sev.Client;

// Refuses a request before it is sent when SevRateLimiter says the limit is reached.
// Put it closest to the network (after SevAuthHandler), so only requests that are really sent are counted.
public class SevRateLimitHandler(SevRateLimiter limiter) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!limiter.TryAcquire(out var retryAfter))
        {
            throw new SevRateLimitExceededException(retryAfter);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

public class SevRateLimitExceededException(TimeSpan retryAfter)
    : Exception($"SEV rate limit reached (counted locally). Try again in {Math.Ceiling(retryAfter.TotalSeconds)} seconds.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}
