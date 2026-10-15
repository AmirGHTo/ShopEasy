using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopEasy.OrderService.Data;

namespace ShopEasy.OrderService.Idempotency;

// Forgets idempotency keys once they are older than Idempotency:RetentionHours, so the table does
// not grow without bound. A single set-based DELETE, so several Order instances can run it at the
// same time without getting in each other's way.
public class IdempotencyCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<IdempotencyOptions> options,
    ILogger<IdempotencyCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retention = TimeSpan.FromHours(options.Value.RetentionHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
                var removed = await PurgeAsync(db, retention, DateTime.UtcNow, stoppingToken);
                if (removed > 0)
                    logger.LogInformation(
                        "Removed {Count} expired idempotency keys (older than {Retention})", removed, retention);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Housekeeping: try again at the next interval (the database may just be restarting)
                logger.LogWarning(ex, "Could not remove expired idempotency keys; will retry");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Returns how many keys were removed
    public static Task<int> PurgeAsync(
        OrderDbContext db, TimeSpan retention, DateTime now, CancellationToken ct = default) =>
        db.IdempotencyRecords
            .Where(r => r.CreatedAt < now - retention)
            .ExecuteDeleteAsync(ct);
}
