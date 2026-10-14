using Microsoft.EntityFrameworkCore;
using ShopEasy.Identity.Data;

namespace ShopEasy.Identity.Storage;

// Expired grants are never read again; without this the table only grows. Every replica runs it,
// which is harmless: deleting rows that are already gone is a no-op.
public class PersistedGrantCleanupService(
    IServiceScopeFactory scopes,
    ILogger<PersistedGrantCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RemoveExpiredAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Removing expired grants failed; will try again in {Interval}", Interval);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    internal async Task<int> RemoveExpiredAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var now = DateTime.UtcNow;
        var removed = await db.PersistedGrants
            .Where(g => g.Expiration != null && g.Expiration < now)
            .ExecuteDeleteAsync(ct);

        if (removed > 0)
            logger.LogInformation("Removed {Count} expired grant(s)", removed);
        return removed;
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
