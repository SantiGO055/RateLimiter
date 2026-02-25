namespace RateLimiter.Domain;

public interface IRateLimitAlgorithm
{
    Task<RateLimitResult> EvaluateAsync(
        string clientKey,
        RateLimitRule rule,
        CancellationToken ct = default);
}
