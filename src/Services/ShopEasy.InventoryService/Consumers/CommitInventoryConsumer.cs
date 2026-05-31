using MassTransit;
using Microsoft.EntityFrameworkCore;
using ShopEasy.InventoryService.Data;
using ShopEasy.Shared.Contracts;
using ShopEasy.Shared.Services;

namespace ShopEasy.InventoryService.Consumers;

public class CommitInventoryConsumer(
    InventoryDbContext db,
    CacheService cache,
    ILogger<CommitInventoryConsumer> logger) : IConsumer<CommitInventoryCommand>
{
    public async Task Consume(ConsumeContext<CommitInventoryCommand> context)
    {
        var cmd = context.Message;
        logger.LogInformation("Committing inventory for Order {OrderId}", cmd.OrderId);

        var reservations = await db.StockTransactions
            .Where(t => t.OrderId == cmd.OrderId &&
                        t.Reason.StartsWith("Reserved for"))
            .ToListAsync();

        if (!reservations.Any())
        {
            logger.LogWarning(
                "No reservations to commit for Order {OrderId}", cmd.OrderId);
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

                inventoryItem.Quantity -= qty;
                inventoryItem.ReservedQty = Math.Max(0, inventoryItem.ReservedQty - qty);
                inventoryItem.UpdatedAt = DateTime.UtcNow;

                db.StockTransactions.Add(new StockTransaction
                {
                    Id = Guid.NewGuid(),
                    ProductId = reservation.ProductId,
                    QuantityChange = -qty,
                    Reason = $"Committed for Order {cmd.OrderId}",
                    OrderId = cmd.OrderId,
                    CreatedAt = DateTime.UtcNow
                });

                affectedProductIds.Add(reservation.ProductId);

                logger.LogInformation(
                    "Committed {Qty} units for Product {ProductId}. New quantity: {New}",
                    qty, reservation.ProductId, inventoryItem.Quantity);
            }

            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            // Invalidate cache for all affected products
            foreach (var productId in affectedProductIds)
                await cache.InvalidateAsync(CacheKeys.Inventory(productId));
            await cache.InvalidateAsync(CacheKeys.InventoryAll);

            logger.LogInformation("Inventory committed for Order {OrderId}", cmd.OrderId);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            logger.LogError(ex,
                "Failed to commit inventory for Order {OrderId}", cmd.OrderId);
            throw;
        }
    }
}