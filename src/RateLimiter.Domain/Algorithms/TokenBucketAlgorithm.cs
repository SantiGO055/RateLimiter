namespace RateLimiter.Domain.Algorithms;

public class TokenBucketAlgorithm(IRateLimitStore store, TimeProvider timeProvider) : IRateLimitAlgorithm
{
    public async Task<RateLimitResult> EvaluateAsync(
        string clientKey, RateLimitRule rule, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var capacity = rule.EffectiveBucketCapacity;
        var refillRate = rule.EffectiveRefillRate;

        var state = await store.GetOrCreateAsync(
            clientKey,
            () => new BucketState(capacity, now));

        lock (state.Lock)
        {
            var elapsed = Math.Max(0, (now - state.LastRefillTimestamp).TotalSeconds);
            var tokensToAdd = elapsed * refillRate;
            state.AvailableTokens = Math.Min(state.AvailableTokens + tokensToAdd, capacity);
            state.LastRefillTimestamp = now;

            if (state.AvailableTokens >= 1)
            {
                state.AvailableTokens -= 1;
                return new RateLimitResult(
                    IsAllowed: true,
                    Limit: rule.Limit,
                    Remaining: (int)Math.Floor(state.AvailableTokens),
                    RetryAfterSeconds: null);
            }

            var tokensNeeded = 1.0 - state.AvailableTokens;
            // Math.Round before Math.Ceiling avoids an IEEE 754 artifact: dividing certain
            // doubles produces results like 4.000000000000001, which Ceiling rounds up to 5.
            // Rounding to 9 decimal places first eliminates that error without affecting precision.
            var retryAfter = refillRate > 0
                ? (int?)Math.Ceiling(Math.Round(tokensNeeded / refillRate, 9))
                : null;

            return new RateLimitResult(
                IsAllowed: false,
                Limit: rule.Limit,
                Remaining: 0,
                RetryAfterSeconds: retryAfter);
        }
    }
}
