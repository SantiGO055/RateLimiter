using System.Collections.Concurrent;
using RateLimiter.Domain;

namespace RateLimiter.Infrastructure.Storage;

public class InMemoryRateLimitStore : IRateLimitStore
{
    private readonly ConcurrentDictionary<string, Lazy<object>> _entries = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastAccess = new();
    private readonly TimeSpan _expireAfter;

    public InMemoryRateLimitStore(TimeSpan? expireAfter = null)
    {
        _expireAfter = expireAfter ?? TimeSpan.FromMinutes(10);
    }

    public Task<T> GetOrCreateAsync<T>(string key, Func<T> factory) where T : class
    {
        // ConcurrentDictionary.GetOrAdd with a factory delegate is NOT atomic: under contention
        // it may invoke the factory more than once. Wrapping in Lazy<T> ensures the factory
        // runs exactly once — the dictionary may create multiple Lazy instances, but only one
        // will win the race and its Value will be computed a single time.
        var lazy = _entries.GetOrAdd(key, _ => new Lazy<object>(() => factory()));
        _lastAccess[key] = DateTime.UtcNow;
        return Task.FromResult((T)lazy.Value);
    }

    public Task RemoveExpiredEntriesAsync(CancellationToken ct = default)
    {
        var threshold = DateTime.UtcNow - _expireAfter;

        foreach (var (key, lastAccessed) in _lastAccess)
        {
            if (ct.IsCancellationRequested)
                break;

            if (lastAccessed < threshold)
            {
                _lastAccess.TryRemove(key, out _);
                _entries.TryRemove(key, out _);
            }
        }

        return Task.CompletedTask;
    }

    internal void SimulateLastAccess(string key, DateTime time)
        => _lastAccess[key] = time;
}
