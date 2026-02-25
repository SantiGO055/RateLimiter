namespace RateLimiter.Domain;

public record RateLimitResult(
    bool IsAllowed,
    int Limit,
    int Remaining,
    int? RetryAfterSeconds);
