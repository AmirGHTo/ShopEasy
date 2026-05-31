using MassTransit;
using Microsoft.EntityFrameworkCore;
using ShopEasy.InventoryService.Data;
using ShopEasy.Shared.Contracts;
using ShopEasy.Shared.Services;

namespace ShopEasy.InventoryService.Consumers;

// Compensating action — called when payment fails.
// Releases the reserved stock back to available.
public class ReleaseInventoryConsumer(
    InventoryDbContext db,
    CacheService cache,
    ILogger<ReleaseInventoryConsumer> logger) : IConsumer<ReleaseInventoryCommand>
{
    public async Task Consume(ConsumeContext<ReleaseInventoryCommand> context)
    {
        var cmd = context.Message;
        logger.LogInformation(
            "Releasing inventory reservation for Order {OrderId}", cmd.OrderId);

        var reservations = await db.StockTransactions
            .Where(t => t.OrderId == cmd.OrderId &&
                        t.Reason.StartsWith("Reserved for"))
            .ToListAsync();

        if (!reservations.Any())
        {
            logger.LogWarning(
                "No reservations found for Order {OrderId} — nothing to release",
                cmd.OrderId);
            await context.Publish(new InventoryReleasedEvent { OrderId = cmd.OrderId });
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var affectedProductIds = new List<Guid>();

            foreach (var reservation in reservations)
            {
                var inventoryItem = await db.InventoryItems
                    .FirstOrDefaultAsync(i => i.ProductId == reservation.ProductId);

                if (inventoryItem is null) continue;

                var qty = Math.Abs(reservation.QuantityChange);

                inventoryItem.ReservedQty = Math.Max(0, inventoryItem.ReservedQty - qty);
                inventoryItem.UpdatedAt = DateTime.UtcNow;

                db.StockTransactions.Add(new StockTransaction
                {
                    Id = Guid.NewGuid(),
                    ProductId = reservation.ProductId,
                    QuantityChange = 0,
                    Reason = $"Released reservation for Order {cmd.OrderId}",
                    OrderId = cmd.OrderId,
                    CreatedAt = DateTime.UtcNow
                });

                affectedProductIds.Add(reservation.ProductId);

                logger.LogInformation(
                    "Released reservation for Product {ProductId} for Order {OrderId}",
                    reservation.ProductId, cmd.OrderId);
            }

            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            // Invalidate cache for all affected products
            foreach (var productId in affectedProductIds)
                await cache.InvalidateAsync(CacheKeys.Inventory(productId));
            await cache.InvalidateAsync(CacheKeys.InventoryAll);

            logger.LogInformation(
                "Inventory released for Order {OrderId}", cmd.OrderId);

            await context.Publish(new InventoryReleasedEvent { OrderId = cmd.OrderId });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            logger.LogError(ex,
                "Failed to release inventory for Order {OrderId}", cmd.OrderId);
            throw;
        }
    }
}