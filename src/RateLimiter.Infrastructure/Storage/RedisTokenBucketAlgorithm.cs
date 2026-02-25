using Polly;
using RateLimiter.Domain;
using StackExchange.Redis;

namespace RateLimiter.Infrastructure.Storage;

public class RedisTokenBucketAlgorithm(
    IConnectionMultiplexer redis,
    TimeProvider timeProvider,
    ResiliencePipeline resilience) : IRateLimitAlgorithm
{
    // A single Lua script executes the full token bucket cycle atomically on the Redis server:
    // read state → calculate refill → consume token → write state. Using GET + SET in two separate
    // commands would open a race window where two API instances read the same token count
    // simultaneously, both allow the request, and both write back — breaking the limit.
    private static readonly LuaScript Script = LuaScript.Prepare("""
        local data = redis.call('HMGET', @key, 'tokens', 'ts')
        local capacity = tonumber(@capacity)
        local refill_rate = tonumber(@refillRate)
        local now = tonumber(@now)
        local tokens, ts

        if data[1] == false then
            tokens = capacity
            ts = now
        else
            tokens = tonumber(data[1])
            ts = tonumber(data[2])
        end

        local elapsed = math.max(0, now - ts)
        tokens = math.min(tokens + elapsed * refill_rate, capacity)

        local is_allowed, remaining, retry_after

        if tokens >= 1 then
            tokens = tokens - 1
            is_allowed = 1
            remaining = math.floor(tokens)
            retry_after = -1
        else
            is_allowed = 0
            remaining = 0
            if refill_rate > 0 then
                local tokens_needed = 1 - tokens
                local rounded = math.floor(tokens_needed / refill_rate * 1000000000 + 0.5) / 1000000000
                retry_after = math.ceil(rounded)
            else
                retry_after = -1
            end
        end

        redis.call('HSET', @key, 'tokens', tostring(tokens), 'ts', tostring(now))
        redis.call('EXPIRE', @key, @ttl)

        return {is_allowed, remaining, retry_after}
        """);

    public async Task<RateLimitResult> EvaluateAsync(
        string clientKey, RateLimitRule rule, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        var ttl = (long)Math.Max(rule.Window.TotalSeconds * 2, 600);

        RedisResult[]? luaResult = null;

        await resilience.ExecuteAsync(async _ =>
        {
            luaResult = ((RedisResult[])await Script.EvaluateAsync(db, new
            {
                key = (RedisKey)clientKey,
                capacity = (RedisValue)(double)rule.EffectiveBucketCapacity,
                refillRate = (RedisValue)rule.EffectiveRefillRate,
                now = (RedisValue)now,
                ttl = (RedisValue)ttl,
            }))!;
        }, ct);

        var isAllowed = (int)luaResult![0] == 1;
        var remaining = (int)luaResult![1];
        var retryAfterRaw = (int)luaResult![2];

        return new RateLimitResult(
            IsAllowed: isAllowed,
            Limit: rule.Limit,
            Remaining: remaining,
            RetryAfterSeconds: retryAfterRaw == -1 ? null : retryAfterRaw);
    }
}
