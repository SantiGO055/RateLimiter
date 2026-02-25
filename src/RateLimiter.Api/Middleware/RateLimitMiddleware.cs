using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RateLimiter.Api.Configuration;
using RateLimiter.Api.Metrics;
using RateLimiter.Domain;

namespace RateLimiter.Api.Middleware;

public class RateLimitMiddleware(
    RequestDelegate next,
    IRateLimitAlgorithm algorithm,
    IOptions<RateLimitOptions> options,
    ILogger<RateLimitMiddleware> logger,
    RateLimitMetrics metrics)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";

        if (!options.Value.Rules.TryGetValue(path, out var ruleConfig))
        {
            await next(context);
            return;
        }

        var clientIp = ExtractClientIp(context);
        var clientKey = $"{clientIp}:{path}";
        var rule = ruleConfig.ToRule();

        RateLimitResult result;
        try
        {
            result = await algorithm.EvaluateAsync(clientKey, rule, context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Rate limit evaluation failed for key {ClientKey}", clientKey);
            metrics.StoreErrors.Add(1, new TagList { { "endpoint", path } });

            if (options.Value.FailOpen)
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        AddRateLimitHeaders(context.Response, result);

        if (result.IsAllowed)
        {
            logger.LogDebug("Request allowed: key={ClientKey} remaining={Remaining}", clientKey, result.Remaining);
            metrics.RequestsAllowed.Add(1, new TagList { { "endpoint", path } });
            await next(context);
            return;
        }

        logger.LogInformation(
            "Request blocked: key={ClientKey} retryAfter={RetryAfterSeconds}s",
            clientKey, result.RetryAfterSeconds);
        metrics.RequestsBlocked.Add(1, new TagList { { "endpoint", path } });
        await WriteRejectedResponse(context.Response, result);
    }

    private static string ExtractClientIp(HttpContext context)
    {
        // X-Forwarded-For is read first to support proxies and load balancers in production,
        // and to allow simulating distinct client IPs in integration tests where
        // WebApplicationFactory always reports loopback as the remote address.
        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwarded))
            return forwarded.Split(',')[0].Trim();

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private static void AddRateLimitHeaders(HttpResponse response, RateLimitResult result)
    {
        response.Headers["X-RateLimit-Limit"] = result.Limit.ToString();
        response.Headers["X-RateLimit-Remaining"] = result.Remaining.ToString();

        if (result.RetryAfterSeconds.HasValue)
            response.Headers["X-RateLimit-Retry-After"] = result.RetryAfterSeconds.Value.ToString();
    }

    private static async Task WriteRejectedResponse(HttpResponse response, RateLimitResult result)
    {
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.ContentType = "application/json";

        var message = result.RetryAfterSeconds.HasValue
            ? $"Too many requests. Please retry after {result.RetryAfterSeconds} seconds."
            : "Too many requests. This endpoint is currently unavailable.";

        var body = JsonSerializer.Serialize(new
        {
            error = "rate_limit_exceeded",
            message
        });

        await response.WriteAsync(body);
    }
}
