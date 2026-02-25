using FluentAssertions;
using RateLimiter.Domain;
using RateLimiter.Infrastructure.Storage;

namespace RateLimiter.Tests.Unit;

public class InMemoryRateLimitStoreTests
{
    // T021 — GetOrCreateAsync retorna misma instancia para mismo key
    [Fact]
    public async Task GetOrCreateAsync_SameKey_ReturnsSameReference()
    {
        var store = new InMemoryRateLimitStore();

        var first = await store.GetOrCreateAsync("key-1", () => new BucketState(10, DateTime.UtcNow));
        var second = await store.GetOrCreateAsync("key-1", () => new BucketState(10, DateTime.UtcNow));

        ReferenceEquals(first, second).Should().BeTrue();
    }

    // T022 — GetOrCreateAsync es thread-safe: factory se invoca exactamente una vez
    [Fact]
    public async Task GetOrCreateAsync_ConcurrentCalls_InvokesFactoryExactlyOnce()
    {
        var store = new InMemoryRateLimitStore();
        int factoryCallCount = 0;

        var tasks = Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => store.GetOrCreateAsync("shared-key", () =>
            {
                Interlocked.Increment(ref factoryCallCount);
                return new BucketState(10, DateTime.UtcNow);
            })))
            .ToList();

        await Task.WhenAll(tasks);

        factoryCallCount.Should().Be(1);
    }

    // T026 — RemoveExpiredEntriesAsync elimina entries viejas y preserva las recientes
    [Fact]
    public async Task RemoveExpiredEntries_RemovesOldEntries_PreservesRecentOnes()
    {
        var now = DateTime.UtcNow;
        var store = new InMemoryRateLimitStore(expireAfter: TimeSpan.FromMinutes(5));

        // Entry "old": último acceso hace 10 minutos (expirada)
        await store.GetOrCreateAsync("old-key", () => new BucketState(10, now));
        store.SimulateLastAccess("old-key", now - TimeSpan.FromMinutes(10));

        // Entry "recent": último acceso hace 1 minuto (vigente)
        await store.GetOrCreateAsync("recent-key", () => new BucketState(10, now));
        store.SimulateLastAccess("recent-key", now - TimeSpan.FromMinutes(1));

        await store.RemoveExpiredEntriesAsync();

        // "old-key" fue eliminada: factory se llama de nuevo (nueva instancia)
        var afterCleanup = await store.GetOrCreateAsync("old-key", () => new BucketState(5, now));
        afterCleanup.AvailableTokens.Should().Be(5);

        // "recent-key" sobrevivió: misma instancia (factory no se vuelve a llamar)
        var recentAfterCleanup = await store.GetOrCreateAsync("recent-key", () => new BucketState(99, now));
        recentAfterCleanup.AvailableTokens.Should().Be(10);
    }
}
