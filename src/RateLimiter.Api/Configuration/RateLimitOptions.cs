using RateLimiter.Domain;

namespace RateLimiter.Api.Configuration;

public class RateLimitOptions
{
    public Dictionary<string, RateLimitRuleConfig> Rules { get; set; } = new();
    public bool FailOpen { get; set; } = true;
}

public class RateLimitRuleConfig
{
    public int Limit { get; init; }
    public TimeSpan Window { get; init; }
    public int? BucketCapacity { get; init; }
    public double? RefillRate { get; init; }

    public RateLimitRule ToRule() => new(Limit, Window, BucketCapacity, RefillRate);
}
