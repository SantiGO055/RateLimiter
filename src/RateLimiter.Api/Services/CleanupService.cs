using RateLimiter.Domain;

namespace RateLimiter.Api.Services;

public class CleanupService(IRateLimitStore store, IConfiguration configuration, ILogger<CleanupService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue("RateLimiting:CleanupIntervalSeconds", 300);
        var interval = TimeSpan.FromSeconds(intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(interval, stoppingToken);

            try
            {
                await store.RemoveExpiredEntriesAsync(stoppingToken);
                logger.LogDebug("Rate limit store cleanup completed");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Rate limit store cleanup failed");
            }
        }
    }
}
