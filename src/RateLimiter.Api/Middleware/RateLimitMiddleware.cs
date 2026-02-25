using System.Text.Json;
using Microsoft.Extensions.Options;
using RateLimiter.Api.Configuration;
using RateLimiter.Domain;

namespace RateLimiter.Api.Middleware;

public class RateLimitMiddleware(
    RequestDelegate next,
    IRateLimitAlgorithm algorithm,
    IOptions<RateLimitOptions> options,
    ILogger<RateLimitMiddleware> logger)
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
            await next(context);
            return;
        }

        await WriteRejectedResponse(context.Response, result);
    }

    private static string ExtractClientIp(HttpContext context)
    {
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
