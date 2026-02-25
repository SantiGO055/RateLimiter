namespace RateLimiter.Domain;

public interface IRateLimitStore
{
    Task<T> GetOrCreateAsync<T>(string key, Func<T> factory) where T : class;
    Task RemoveExpiredEntriesAsync(CancellationToken ct = default);
}
