using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using RateLimiter.Domain;
using RateLimiter.Domain.Algorithms;
using RateLimiter.Infrastructure.Storage;

namespace RateLimiter.Tests.Unit;

public class TokenBucketAlgorithmTests
{
    private static RateLimitRule MakeRule(
        int limit = 10,
        int? bucketCapacity = null,
        double? refillRate = null,
        int windowSeconds = 60)
        => new(limit, TimeSpan.FromSeconds(windowSeconds), bucketCapacity, refillRate);

    private static (TokenBucketAlgorithm algorithm, FakeTimeProvider clock) BuildSut(
        RateLimitRule? rule = null)
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryRateLimitStore();
        var algorithm = new TokenBucketAlgorithm(store, clock);
        return (algorithm, clock);
    }

    // T011 — BS1 Scenario 1: primer request retorna allowed con remaining = capacity - 1
    [Fact]
    public async Task FirstRequest_ReturnsAllowed_WithRemainingEqualToCapacityMinusOne()
    {
        var (sut, _) = BuildSut();
        var rule = MakeRule(limit: 10);

        var result = await sut.EvaluateAsync("client-1", rule);

        result.IsAllowed.Should().BeTrue();
        result.Limit.Should().Be(10);
        result.Remaining.Should().Be(9);
        result.RetryAfterSeconds.Should().BeNull();
    }

    // T012 — BS2 Scenario 1: request con 0 tokens retorna denied con RetryAfter > 0
    [Fact]
    public async Task RequestWithNoTokens_ReturnsDenied_WithRetryAfterGreaterThanZero()
    {
        var (sut, _) = BuildSut();
        var rule = MakeRule(limit: 10);

        for (int i = 0; i < 10; i++)
            await sut.EvaluateAsync("client-1", rule);

        var result = await sut.EvaluateAsync("client-1", rule);

        result.IsAllowed.Should().BeFalse();
        result.Remaining.Should().Be(0);
        result.RetryAfterSeconds.Should().BeGreaterThan(0);
    }

    // T013 — BS2 Scenario 3: requests rechazados no consumen tokens
    [Fact]
    public async Task RejectedRequests_DoNotConsumeTokens()
    {
        var (sut, clock) = BuildSut();
        var rule = MakeRule(limit: 10, refillRate: 10.0 / 60.0);

        for (int i = 0; i < 10; i++)
            await sut.EvaluateAsync("client-1", rule);

        for (int i = 0; i < 5; i++)
        {
            var denied = await sut.EvaluateAsync("client-1", rule);
            denied.IsAllowed.Should().BeFalse();
        }

        // Avanzar exactamente 6 segundos: recarga exactamente 1 token (10/60 * 6 = 1)
        clock.Advance(TimeSpan.FromSeconds(6));
        var result = await sut.EvaluateAsync("client-1", rule);

        result.IsAllowed.Should().BeTrue();
        result.Remaining.Should().Be(0);
    }

    // T014 — BS3 Scenario 1: refill proporcional al tiempo transcurrido
    [Fact]
    public async Task AfterWaiting_TokensRefillProportionally()
    {
        var (sut, clock) = BuildSut();
        // 10 tokens/minuto = 10/60 tokens/segundo
        var rule = MakeRule(limit: 10, refillRate: 10.0 / 60.0);

        for (int i = 0; i < 10; i++)
            await sut.EvaluateAsync("client-1", rule);

        // Después de 30s: 30 * (10/60) = 5 tokens recargados
        clock.Advance(TimeSpan.FromSeconds(30));
        var result = await sut.EvaluateAsync("client-1", rule);

        result.IsAllowed.Should().BeTrue();
        result.Remaining.Should().Be(4); // 5 recargados, 1 consumido
    }

    // T015 — BS3 Scenario 2: refill no excede la capacidad del bucket
    [Fact]
    public async Task Refill_DoesNotExceedBucketCapacity()
    {
        var (sut, clock) = BuildSut();
        var rule = MakeRule(limit: 10, refillRate: 10.0 / 60.0);

        // Consumir 2 tokens → quedan 8
        await sut.EvaluateAsync("client-1", rule);
        await sut.EvaluateAsync("client-1", rule);

        // Esperar 60s: agregaría 10 tokens, pero capacity es 10 → se clampea a 10
        clock.Advance(TimeSpan.FromSeconds(60));
        var result = await sut.EvaluateAsync("client-1", rule);

        result.IsAllowed.Should().BeTrue();
        result.Remaining.Should().Be(9); // min(8 + 10, 10) - 1 = 9
    }

    // T016 — BS3 Scenario 3: tokens fraccionarios no alcanzan para consumir
    [Fact]
    public async Task FractionalTokens_AreNotSufficientToConsume()
    {
        var (sut, clock) = BuildSut();
        // 1 token/segundo
        var rule = MakeRule(limit: 1, bucketCapacity: 1, refillRate: 1.0, windowSeconds: 1);

        await sut.EvaluateAsync("client-1", rule); // consume el único token

        // 500ms → 0.5 tokens acumulados, no suficiente para 1
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var result = await sut.EvaluateAsync("client-1", rule);

        result.IsAllowed.Should().BeFalse();
    }

    // T017 — BS1 Scenario 3: dos clientes tienen buckets independientes
    [Fact]
    public async Task TwoClients_HaveIndependentBuckets()
    {
        var (sut, _) = BuildSut();
        var rule = MakeRule(limit: 10);

        // Agotar todos los tokens de cliente A
        for (int i = 0; i < 10; i++)
            await sut.EvaluateAsync("client-a", rule);

        var resultA = await sut.EvaluateAsync("client-a", rule);
        var resultB = await sut.EvaluateAsync("client-b", rule);

        resultA.IsAllowed.Should().BeFalse();
        resultA.Remaining.Should().Be(0);

        resultB.IsAllowed.Should().BeTrue();
        resultB.Remaining.Should().Be(9);
    }

    // T018 — BS2 Scenario 2: RetryAfter refleja tiempo exacto hasta el próximo token
    [Fact]
    public async Task RetryAfter_ReflectsExactTimeToNextToken()
    {
        var (sut, clock) = BuildSut();
        // 10 tokens/minuto → 1 token cada 6 segundos
        var rule = MakeRule(limit: 10, refillRate: 10.0 / 60.0);

        for (int i = 0; i < 10; i++)
            await sut.EvaluateAsync("client-1", rule);

        // Avanzar 2 segundos → se recargan 2 * (10/60) ≈ 0.333 tokens (< 1, sigue bloqueado)
        clock.Advance(TimeSpan.FromSeconds(2));
        var result = await sut.EvaluateAsync("client-1", rule);

        result.IsAllowed.Should().BeFalse();
        // Faltan (1 - 0.333) / (10/60) ≈ 4 segundos para el próximo token
        result.RetryAfterSeconds.Should().Be(4);
    }

    // T023 — BS4 Scenario 1: 20 requests concurrentes respetan el límite de 10
    [Fact]
    public async Task TwentyConcurrentRequests_ExactlyTenAllowed()
    {
        var (sut, _) = BuildSut();
        var rule = MakeRule(limit: 10);

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => sut.EvaluateAsync("client-1", rule)))
            .ToList();

        var results = await Task.WhenAll(tasks);

        results.Count(r => r.IsAllowed).Should().Be(10);
        results.Count(r => !r.IsAllowed).Should().Be(10);
    }

    // T024 — BS4 Scenario 2: 1 token restante + 5 concurrentes = exactamente 1 allowed
    [Fact]
    public async Task OneTokenLeft_FiveConcurrent_ExactlyOneAllowed()
    {
        var (sut, _) = BuildSut();
        var rule = MakeRule(limit: 10);

        // Consumir 9 tokens → queda 1
        for (int i = 0; i < 9; i++)
            await sut.EvaluateAsync("client-1", rule);

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => Task.Run(() => sut.EvaluateAsync("client-1", rule)))
            .ToList();

        var results = await Task.WhenAll(tasks);

        results.Count(r => r.IsAllowed).Should().Be(1);
        results.Count(r => !r.IsAllowed).Should().Be(4);
    }

    // T025 — BS4 Scenario 3: clientes distintos no se bloquean entre sí bajo concurrencia
    [Fact]
    public async Task DifferentClients_DoNotBlockEachOther_UnderConcurrency()
    {
        var (sut, _) = BuildSut();
        var rule = MakeRule(limit: 5);

        var tasksA = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => sut.EvaluateAsync("client-a", rule)))
            .ToList();

        var tasksB = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => sut.EvaluateAsync("client-b", rule)))
            .ToList();

        var resultsA = await Task.WhenAll(tasksA);
        var resultsB = await Task.WhenAll(tasksB);

        resultsA.Count(r => r.IsAllowed).Should().Be(5);
        resultsB.Count(r => r.IsAllowed).Should().Be(5);
    }
}
