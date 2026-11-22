namespace Sev.Client;

// Tracks SEV's limit of 10 calls per 5 minutes per customer, login included, by remembering when
// each recent call was sent. SEV doesn't return an error when the limit is exceeded (it answers
// 200 OK with empty data), so counting locally is the only reliable way to know.
// Share one instance (singleton) between every HttpClient that calls SEV.
public class SevRateLimiter
{
    public const int DefaultMaxCalls = 10;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time;
    private readonly int _maxCalls;
    private readonly TimeSpan _window;
    private readonly Queue<DateTimeOffset> _sentAt = new();
    private readonly Lock _lock = new();

    public SevRateLimiter(TimeProvider? timeProvider = null, int maxCalls = DefaultMaxCalls, TimeSpan? window = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCalls);
        _time = timeProvider ?? TimeProvider.System;
        _maxCalls = maxCalls;
        _window = window ?? DefaultWindow;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_window, TimeSpan.Zero, nameof(window));
    }

    // Records a call and returns true if it is within the limit. Otherwise records nothing and
    // returns false, with retryAfter set to when the oldest call leaves the window.
    public bool TryAcquire(out TimeSpan retryAfter)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            while (_sentAt.Count > 0 && now - _sentAt.Peek() >= _window)
            {
                _sentAt.Dequeue();
            }

            if (_sentAt.Count < _maxCalls)
            {
                _sentAt.Enqueue(now);
                retryAfter = TimeSpan.Zero;
                return true;
            }

            retryAfter = _sentAt.Peek() + _window - now;
            return false;
        }
    }
}
