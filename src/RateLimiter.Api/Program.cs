using Polly;
using Polly.CircuitBreaker;
using RateLimiter.Api.Configuration;
using RateLimiter.Api.Metrics;
using RateLimiter.Api.Middleware;
using RateLimiter.Api.Services;
using RateLimiter.Domain;
using RateLimiter.Domain.Algorithms;
using RateLimiter.Infrastructure.Storage;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection("RateLimiting"));
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<RateLimitMetrics>();

var storeType = builder.Configuration.GetValue<string>("RateLimiting:Store") ?? "InMemory";

if (string.Equals(storeType, "Redis", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis es requerido cuando RateLimiting:Store=Redis");

    builder.Services.AddSingleton<IConnectionMultiplexer>(
        _ => ConnectionMultiplexer.Connect(connectionString));

    builder.Services.AddSingleton(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<RedisTokenBucketAlgorithm>>();
        var cb = builder.Configuration.GetSection("RateLimiting:CircuitBreaker");

        return new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                MinimumThroughput = cb.GetValue("FailureThreshold", 5),
                FailureRatio = 1.0,
                SamplingDuration = TimeSpan.FromSeconds(cb.GetValue("SamplingDurationSeconds", 30)),
                BreakDuration = TimeSpan.FromSeconds(cb.GetValue("BreakDurationSeconds", 15)),
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is not null),
                OnOpened = args =>
                {
                    logger.LogWarning(
                        "Redis circuit breaker opened for {Duration}s. Requests will pass via fail-open until the circuit resets.",
                        args.BreakDuration.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    logger.LogInformation("Redis circuit breaker closed. Resuming normal Redis operation.");
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = _ =>
                {
                    logger.LogDebug("Redis circuit breaker half-opened. Testing connection.");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    });

    builder.Services.AddSingleton<IRateLimitAlgorithm, RedisTokenBucketAlgorithm>();
}
else
{
    builder.Services.AddSingleton<IRateLimitStore, InMemoryRateLimitStore>();
    builder.Services.AddSingleton<IRateLimitAlgorithm, TokenBucketAlgorithm>();
    builder.Services.AddHostedService<CleanupService>();
}

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
startupLogger.LogInformation("Rate limiting store: {StoreType}", storeType);

app.UseMiddleware<RateLimitMiddleware>();

app.MapGet("/api/resource", () => Results.Ok(new { message = "OK" }));
app.MapGet("/api/unrestricted", () => Results.Ok(new { message = "OK" }));

app.Run();

public partial class Program;
