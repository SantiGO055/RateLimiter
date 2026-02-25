using System.Diagnostics.Metrics;

namespace RateLimiter.Api.Metrics;

public sealed class RateLimitMetrics : IDisposable
{
    private readonly Meter _meter = new("RateLimiter");

    public readonly Counter<long> RequestsAllowed;
    public readonly Counter<long> RequestsBlocked;
    public readonly Counter<long> StoreErrors;

    public RateLimitMetrics()
    {
        RequestsAllowed = _meter.CreateCounter<long>(
            "ratelimit.requests.allowed",
            description: "Requests that passed rate limiting");

        RequestsBlocked = _meter.CreateCounter<long>(
            "ratelimit.requests.blocked",
            description: "Requests rejected with 429");

        StoreErrors = _meter.CreateCounter<long>(
            "ratelimit.store.errors",
            description: "Rate limit store evaluation failures");
    }

    public void Dispose() => _meter.Dispose();
}
