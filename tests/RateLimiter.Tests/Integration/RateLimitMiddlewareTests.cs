using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using RateLimiter.Domain;
using RateLimiter.Domain.Algorithms;
using RateLimiter.Infrastructure.Storage;

namespace RateLimiter.Tests.Integration;

public class RateLimitMiddlewareTests
{
    private static WebApplicationFactory<Program> CreateFactory(
        Action<IServiceCollection>? configureServices = null,
        Dictionary<string, string?>? config = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (config is not null)
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(config));

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRateLimitAlgorithm>();
                services.RemoveAll<IRateLimitStore>();
                services.AddSingleton<IRateLimitStore, InMemoryRateLimitStore>();
                services.AddSingleton<IRateLimitAlgorithm, TokenBucketAlgorithm>();
            });

            if (configureServices is not null)
                builder.ConfigureTestServices(configureServices);
        });
    }

    private static Dictionary<string, string?> RuleConfig(
        string path = "/api/resource", int limit = 10) => new()
    {
        [$"RateLimiting:Rules:{path}:Limit"] = limit.ToString(),
        [$"RateLimiting:Rules:{path}:Window"] = "00:01:00",
        [$"RateLimiting:Rules:{path}:BucketCapacity"] = limit.ToString(),
    };

    // T028 — BS5 Scenario 1+2: fail-open cuando el algoritmo lanza excepción
    [Fact]
    public async Task AlgorithmThrows_FailOpenDefault_Returns200WithoutRateLimitHeaders()
    {
        using var factory = CreateFactory(
            configureServices: s => s.AddSingleton<IRateLimitAlgorithm>(new ThrowingAlgorithm()),
            config: RuleConfig());

        var response = await factory.CreateClient().GetAsync("/api/resource");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Should().NotContainKey("X-RateLimit-Limit");
        response.Headers.Should().NotContainKey("X-RateLimit-Remaining");
    }

    // T029 — BS5 Scenario 3: fail-closed retorna 503 cuando el algoritmo falla
    [Fact]
    public async Task AlgorithmThrows_FailClosedConfig_Returns503()
    {
        var config = RuleConfig();
        config["RateLimiting:FailOpen"] = "false";

        using var factory = CreateFactory(
            configureServices: s => s.AddSingleton<IRateLimitAlgorithm>(new ThrowingAlgorithm()),
            config: config);

        var response = await factory.CreateClient().GetAsync("/api/resource");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // T030 — FR-008: endpoint sin regla pasa sin headers de rate limit
    [Fact]
    public async Task EndpointWithNoRule_PassesThrough_WithoutRateLimitHeaders()
    {
        using var factory = CreateFactory();

        var response = await factory.CreateClient().GetAsync("/api/unrestricted");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Should().NotContainKey("X-RateLimit-Limit");
        response.Headers.Should().NotContainKey("X-RateLimit-Remaining");
        response.Headers.Should().NotContainKey("X-RateLimit-Retry-After");
    }

    // T034 — BS1 Scenario 1: primer request retorna 200 con headers correctos
    [Fact]
    public async Task FirstRequest_Returns200_WithCorrectRateLimitHeaders()
    {
        using var factory = CreateFactory(config: RuleConfig(limit: 10));
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/resource");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-RateLimit-Limit").First().Should().Be("10");
        response.Headers.GetValues("X-RateLimit-Remaining").First().Should().Be("9");
        response.Headers.Should().NotContainKey("X-RateLimit-Retry-After");
    }

    // T035 — BS2 Scenario 1: request excedido retorna 429 con body JSON y headers
    [Fact]
    public async Task ExceededRequest_Returns429_WithJsonBodyAndHeaders()
    {
        using var factory = CreateFactory(config: RuleConfig(limit: 10));
        var client = factory.CreateClient();

        for (int i = 0; i < 10; i++)
            await client.GetAsync("/api/resource");

        var response = await client.GetAsync("/api/resource");
        var body = JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        body.GetProperty("error").GetString().Should().Be("rate_limit_exceeded");
        body.GetProperty("message").GetString().Should().NotBeNullOrEmpty();
        response.Headers.GetValues("X-RateLimit-Limit").First().Should().Be("10");
        response.Headers.GetValues("X-RateLimit-Remaining").First().Should().Be("0");
        response.Headers.Should().ContainKey("X-RateLimit-Retry-After");
    }

    // T036 — BS3 Scenario 1: tokens se recargan después de esperar (FakeTimeProvider)
    [Fact]
    public async Task AfterTimeAdvances_TokensRefill_AllowingNewRequests()
    {
        var fakeTime = new FakeTimeProvider();
        using var factory = CreateFactory(
            configureServices: s => s.AddSingleton<TimeProvider>(fakeTime),
            config: RuleConfig(limit: 10));

        var client = factory.CreateClient();

        for (int i = 0; i < 10; i++)
            await client.GetAsync("/api/resource");

        var blockedResponse = await client.GetAsync("/api/resource");
        blockedResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // Avanzar 30s: recarga 30 * (10/60) = 5 tokens
        fakeTime.Advance(TimeSpan.FromSeconds(30));

        var response = await client.GetAsync("/api/resource");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-RateLimit-Remaining").First().Should().Be("4");
    }

    // T037 — BS4 Scenario 1: 20 requests concurrentes respetan el límite de 10
    [Fact]
    public async Task TwentyConcurrentRequests_ExactlyTenAllowed()
    {
        using var factory = CreateFactory(config: RuleConfig(limit: 10));
        var client = factory.CreateClient();

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => client.GetAsync("/api/resource"))
            .ToList();

        var responses = await Task.WhenAll(tasks);

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(10);
        responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests).Should().Be(10);
    }

    // T038 — BS1 Scenario 3: dos IPs distintas tienen límites independientes
    [Fact]
    public async Task TwoDifferentIps_HaveIndependentRateLimits()
    {
        using var factory = CreateFactory(config: RuleConfig(limit: 3));
        var client = factory.CreateClient();

        // IP A: agotar los 3 tokens
        for (int i = 0; i < 3; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/resource");
            req.Headers.Add("X-Forwarded-For", "10.0.0.1");
            await client.SendAsync(req);
        }

        // IP A: bloqueada
        var reqA = new HttpRequestMessage(HttpMethod.Get, "/api/resource");
        reqA.Headers.Add("X-Forwarded-For", "10.0.0.1");
        var responseA = await client.SendAsync(reqA);

        // IP B: bucket independiente, no bloqueada
        var reqB = new HttpRequestMessage(HttpMethod.Get, "/api/resource");
        reqB.Headers.Add("X-Forwarded-For", "10.0.0.2");
        var responseB = await client.SendAsync(reqB);

        responseA.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        responseB.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // T040 — Edge: regla con límite 0 rechaza siempre
    [Fact]
    public async Task RuleWithLimitZero_RejectsAllRequests()
    {
        using var factory = CreateFactory(config: RuleConfig(limit: 0));

        var response = await factory.CreateClient().GetAsync("/api/resource");

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    private sealed class ThrowingAlgorithm : IRateLimitAlgorithm
    {
        public Task<RateLimitResult> EvaluateAsync(
            string clientKey, RateLimitRule rule, CancellationToken ct = default)
            => throw new InvalidOperationException("Store failure simulated");
    }
}
