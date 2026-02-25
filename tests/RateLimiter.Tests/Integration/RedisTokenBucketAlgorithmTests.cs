using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Polly;
using RateLimiter.Domain;
using RateLimiter.Infrastructure.Storage;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace RateLimiter.Tests.Integration;

public class RedisTokenBucketAlgorithmTests : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder().Build();
    private IConnectionMultiplexer _mux = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _mux = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await _mux.DisposeAsync();
        await _container.DisposeAsync();
    }

    private static RateLimitRule MakeRule(
        int limit = 10,
        int? bucketCapacity = null,
        double? refillRate = null,
        int windowSeconds = 60)
        => new(limit, TimeSpan.FromSeconds(windowSeconds), bucketCapacity, refillRate);

    private static readonly ResiliencePipeline NoOpPipeline = new ResiliencePipelineBuilder().Build();

    private RedisTokenBucketAlgorithm BuildSut(FakeTimeProvider clock)
        => new(_mux, clock, NoOpPipeline);

    // T-R01 — primer request permitido con remaining correcto
    [Fact]
    public async Task FirstRequest_ReturnsAllowed_WithRemainingEqualToCapacityMinusOne()
    {
        var clock = new FakeTimeProvider();
        var sut = BuildSut(clock);
        var rule = MakeRule(limit: 10);

        var result = await sut.EvaluateAsync("r01:client", rule);

        result.IsAllowed.Should().BeTrue();
        result.Limit.Should().Be(10);
        result.Remaining.Should().Be(9);
        result.RetryAfterSeconds.Should().BeNull();
    }

    // T-R02 — request con 0 tokens retorna denied con RetryAfter > 0
    [Fact]
    public async Task RequestWithNoTokens_ReturnsDenied_WithRetryAfterGreaterThanZero()
    {
        var clock = new FakeTimeProvider();
        var sut = BuildSut(clock);
        var rule = MakeRule(limit: 10);

        for (int i = 0; i < 10; i++)
            await sut.EvaluateAsync("r02:client", rule);

        var result = await sut.EvaluateAsync("r02:client", rule);

        result.IsAllowed.Should().BeFalse();
        result.Remaining.Should().Be(0);
        result.RetryAfterSeconds.Should().BeGreaterThan(0);
    }

    // T-R03 — tokens se recargan proporcionalmente al tiempo (FakeTimeProvider)
    [Fact]
    public async Task AfterTimeAdvances_TokensRefill_AllowingNewRequests()
    {
        var clock = new FakeTimeProvider();
        var sut = BuildSut(clock);
        var rule = MakeRule(limit: 10, refillRate: 10.0 / 60.0);

        for (int i = 0; i < 10; i++)
            await sut.EvaluateAsync("r03:client", rule);

        clock.Advance(TimeSpan.FromSeconds(30));
        var result = await sut.EvaluateAsync("r03:client", rule);

        result.IsAllowed.Should().BeTrue();
        result.Remaining.Should().Be(4);
    }

    // T-R04 — refill no excede la capacidad del bucket
    [Fact]
    public async Task Refill_DoesNotExceedBucketCapacity()
    {
        var clock = new FakeTimeProvider();
        var sut = BuildSut(clock);
        var rule = MakeRule(limit: 10, refillRate: 10.0 / 60.0);

        await sut.EvaluateAsync("r04:client", rule);
        await sut.EvaluateAsync("r04:client", rule);

        clock.Advance(TimeSpan.FromSeconds(60));
        var result = await sut.EvaluateAsync("r04:client", rule);

        result.IsAllowed.Should().BeTrue();
        result.Remaining.Should().Be(9);
    }

    // T-R05 — dos clientes tienen buckets independientes en Redis
    [Fact]
    public async Task TwoClients_HaveIndependentBuckets()
    {
        var clock = new FakeTimeProvider();
        var sut = BuildSut(clock);
        var rule = MakeRule(limit: 10);

        for (int i = 0; i < 10; i++)
            await sut.EvaluateAsync("r05:client-a", rule);

        var resultA = await sut.EvaluateAsync("r05:client-a", rule);
        var resultB = await sut.EvaluateAsync("r05:client-b", rule);

        resultA.IsAllowed.Should().BeFalse();
        resultB.IsAllowed.Should().BeTrue();
        resultB.Remaining.Should().Be(9);
    }

    // T-R06 — 20 requests concurrentes respetan el límite de 10 (atomicidad del Lua script)
    [Fact]
    public async Task TwentyConcurrentRequests_ExactlyTenAllowed()
    {
        var clock = new FakeTimeProvider();
        var sut = BuildSut(clock);
        var rule = MakeRule(limit: 10);

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => sut.EvaluateAsync("r06:client", rule)))
            .ToList();

        var results = await Task.WhenAll(tasks);

        results.Count(r => r.IsAllowed).Should().Be(10);
        results.Count(r => !r.IsAllowed).Should().Be(10);
    }

    // T-R07 — regla con límite 0: RetryAfterSeconds es null (endpoint deshabilitado)
    [Fact]
    public async Task RuleWithLimitZero_RetryAfterIsNull()
    {
        var clock = new FakeTimeProvider();
        var sut = BuildSut(clock);
        var rule = MakeRule(limit: 0);

        var result = await sut.EvaluateAsync("r07:client", rule);

        result.IsAllowed.Should().BeFalse();
        result.RetryAfterSeconds.Should().BeNull();
    }

    // T-R08 — RetryAfter refleja tiempo exacto hasta el próximo token
    [Fact]
    public async Task RetryAfter_ReflectsExactTimeToNextToken()
    {
        var clock = new FakeTimeProvider();
        var sut = BuildSut(clock);
        var rule = MakeRule(limit: 10, refillRate: 10.0 / 60.0);

        for (int i = 0; i < 10; i++)
            await sut.EvaluateAsync("r08:client", rule);

        clock.Advance(TimeSpan.FromSeconds(2));
        var result = await sut.EvaluateAsync("r08:client", rule);

        result.IsAllowed.Should().BeFalse();
        result.RetryAfterSeconds.Should().Be(4);
    }
}
