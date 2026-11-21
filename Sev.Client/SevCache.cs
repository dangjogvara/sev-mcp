using Sev.Client.Models;

namespace Sev.Client;

// In-memory store behind SevDataService. Register as a singleton so the data outlives each request;
// SevDataService itself is short-lived because it holds a SevClient (and with it an HttpClient).
// Grows by one entry per meter per day queried, which is small for a single account.
public class SevCache(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan MeterListLifetime = TimeSpan.FromHours(6);

    // A day is only cached when it has at least this many hourly readings, so a day SEV hasn't
    // finished delivering isn't kept half-empty. 23 allows for the short day when summer time starts.
    public const int MinReadingsForCompleteDay = 23;

    internal TimeProvider Time { get; } = timeProvider ?? TimeProvider.System;

    // Held while checking the cache and fetching, so concurrent tool calls don't fetch the same data twice.
    internal SemaphoreSlim Lock { get; } = new(1, 1);

    internal IReadOnlyList<Customer>? Meters { get; set; }

    internal DateTimeOffset MetersFetchedAt { get; set; }

    internal Dictionary<(int MeterId, DateOnly Day), IReadOnlyList<UsageReading>> UsageDays { get; } = [];
}
