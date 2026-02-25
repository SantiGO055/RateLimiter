namespace RateLimiter.Domain;

public record RateLimitRule(
    int Limit,
    TimeSpan Window,
    int? BucketCapacity,
    double? RefillRate)
{
    public int EffectiveBucketCapacity => BucketCapacity ?? Limit;
    public double EffectiveRefillRate => RefillRate ?? (Limit / Window.TotalSeconds);
}
