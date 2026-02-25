namespace RateLimiter.Domain;

public class BucketState
{
    public double AvailableTokens { get; set; }
    public DateTime LastRefillTimestamp { get; set; }

    public readonly object Lock = new();

    public BucketState(double initialTokens, DateTime timestamp)
    {
        AvailableTokens = initialTokens;
        LastRefillTimestamp = timestamp;
    }
}
