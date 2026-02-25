using RateLimiter.Api.Configuration;
using RateLimiter.Api.Middleware;
using RateLimiter.Api.Services;
using RateLimiter.Domain;
using RateLimiter.Domain.Algorithms;
using RateLimiter.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RateLimitOptions>(
    builder.Configuration.GetSection("RateLimiting"));

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IRateLimitStore, InMemoryRateLimitStore>();
builder.Services.AddSingleton<IRateLimitAlgorithm, TokenBucketAlgorithm>();
builder.Services.AddHostedService<CleanupService>();

var app = builder.Build();

app.UseMiddleware<RateLimitMiddleware>();

app.MapGet("/api/resource", () => Results.Ok(new { message = "OK" }));
app.MapGet("/api/unrestricted", () => Results.Ok(new { message = "OK" }));

app.Run();

public partial class Program;
