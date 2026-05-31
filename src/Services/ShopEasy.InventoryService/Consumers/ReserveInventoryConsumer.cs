using MassTransit;
using Microsoft.EntityFrameworkCore;
using ShopEasy.InventoryService.Data;
using ShopEasy.InventoryService.Metrics;
using ShopEasy.Shared.Contracts;
using ShopEasy.Shared.Services;

namespace ShopEasy.InventoryService.Consumers;

public class ReserveInventoryConsumer(
    InventoryDbContext db,
    CacheService cache,
    ILogger<ReserveInventoryConsumer> logger) : IConsumer<ReserveInventoryCommand>
{
    public async Task Consume(ConsumeContext<ReserveInventoryCommand> context)
    {
        var cmd = context.Message;
        logger.LogInformation("Reserving inventory for Order {OrderId}", cmd.OrderId);

        // Idempotency check
        var alreadyProcessed = await db.ProcessedEvents
            .AnyAsync(e => e.EventId == cmd.OrderId);

        if (alreadyProcessed)
        {
            logger.LogWarning(
                "Duplicate ReserveInventoryCommand for Order {OrderId} — skipping",
                cmd.OrderId);
            await context.Publish(new InventoryReservedEvent { OrderId = cmd.OrderId });
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            foreach (var lineItem in cmd.Items)
            {
                var inventoryItem = await db.InventoryItems
                    .FirstOrDefaultAsync(i => i.ProductId == lineItem.ProductId);

                if (inventoryItem is null || inventoryItem.AvailableQuantity < lineItem.Quantity)
                {
                    await transaction.RollbackAsync();
                    logger.LogError(
                        "Insufficient stock for Product {ProductId}", lineItem.ProductId);

                    await context.Publish(new InventoryReservationFailedEvent
                    {
                        OrderId = cmd.OrderId,
                        Reason = $"Insufficient stock for {lineItem.ProductName}"
                    });
                    return;
                }

                inventoryItem.ReservedQty += lineItem.Quantity;
                inventoryItem.UpdatedAt = DateTime.UtcNow;

                db.StockTransactions.Add(new StockTransaction
                {
                    Id = Guid.NewGuid(),
                    ProductId = lineItem.ProductId,
                    QuantityChange = -lineItem.Quantity,
                    Reason = $"Reserved for Order {cmd.OrderId}",
                    OrderId = cmd.OrderId,
                    CreatedAt = DateTime.UtcNow
                });

                logger.LogInformation(
                    "Reserved {Qty} units of {Product} for Order {OrderId}",
                    lineItem.Quantity, lineItem.ProductName, cmd.OrderId);
            }

            db.ProcessedEvents.Add(new ProcessedEvent
            {
                EventId = cmd.OrderId,
                EventType = nameof(ReserveInventoryCommand),
                ProcessedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            InventoryMetrics.RecordReservation(true);

            // Invalidate inventory cache — reserved quantities changed
            foreach (var lineItem in cmd.Items)
                await cache.InvalidateAsync(CacheKeys.Inventory(lineItem.ProductId));
            await cache.InvalidateAsync(CacheKeys.InventoryAll);

            logger.LogInformation("Inventory reserved for Order {OrderId}", cmd.OrderId);
            await context.Publish(new InventoryReservedEvent { OrderId = cmd.OrderId });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            InventoryMetrics.RecordReservation(false);
            logger.LogError(ex,
                "Failed to reserve inventory for Order {OrderId}", cmd.OrderId);
            throw;
        }
    }
}