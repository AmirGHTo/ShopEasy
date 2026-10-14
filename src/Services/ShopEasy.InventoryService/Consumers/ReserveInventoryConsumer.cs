using MassTransit;
using Microsoft.EntityFrameworkCore;
using ShopEasy.InventoryService.Data;
using ShopEasy.InventoryService.Metrics;
using ShopEasy.Shared.Contracts;
using ShopEasy.Shared.Services;

namespace ShopEasy.InventoryService.Consumers;

// Reserves stock for an order: available quantity is held back (ReservedQty) but not
// deducted yet — that happens in CommitInventoryConsumer once payment succeeded.
//
// Concurrency: the affected InventoryItem rows are locked (SELECT ... FOR UPDATE) before
// stock is checked, so concurrent reservations — on this or any other instance — are
// serialized per product and can never oversell. The duplicate-message check also runs
// under those locks, so two copies of the same command cannot both reserve.
public class ReserveInventoryConsumer(
    InventoryDbContext db,
    IInventoryLocking locking,
    CacheService cache,
    ILogger<ReserveInventoryConsumer> logger) : IConsumer<ReserveInventoryCommand>
{
    private sealed record Line(Guid ProductId, string ProductName, int Quantity);

    public async Task Consume(ConsumeContext<ReserveInventoryCommand> context)
    {
        var cmd = context.Message;
        var ct = context.CancellationToken;
        logger.LogInformation("Reserving inventory for Order {OrderId}", cmd.OrderId);

        // A product can appear on several lines: reserve each product once, with the total.
        var lines = cmd.Items
            .GroupBy(i => i.ProductId)
            .Select(g => new Line(g.Key, g.First().ProductName, g.Sum(i => i.Quantity)))
            .OrderBy(l => l.ProductId)
            .ToList();

        string? failureReason = null;
        var duplicate = false;

        await using var transaction = await db.Database.BeginOrJoinAsync(ct);
        try
        {
            var items = await locking.LockItemsAsync(db, lines.Select(l => l.ProductId).ToList(), ct);

            // Idempotency — checked while holding the locks
            duplicate = await db.ProcessedEvents.AnyAsync(e => e.EventId == cmd.OrderId, ct);

            if (!duplicate)
            {
                failureReason = FindProblem(lines, items);

                if (failureReason is null)
                {
                    var now = DateTime.UtcNow;
                    foreach (var line in lines)
                    {
                        var item = items.First(i => i.ProductId == line.ProductId);
                        item.ReservedQty += line.Quantity;
                        item.UpdatedAt = now;

                        db.StockTransactions.Add(new StockTransaction
                        {
                            Id = Guid.NewGuid(),
                            ProductId = line.ProductId,
                            QuantityChange = -line.Quantity,
                            Reason = StockReasons.Reserved(cmd.OrderId),
                            OrderId = cmd.OrderId,
                            CreatedAt = now
                        });

                        logger.LogInformation(
                            "Reserved {Qty} units of {Product} for Order {OrderId}",
                            line.Quantity, line.ProductName, cmd.OrderId);
                    }

                    db.ProcessedEvents.Add(new ProcessedEvent
                    {
                        EventId = cmd.OrderId,
                        EventType = nameof(ReserveInventoryCommand),
                        ProcessedAt = now
                    });

                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                }
            }

            // Nothing was changed (duplicate or rejected) — release the row locks
            if (duplicate || failureReason is not null)
                await transaction.RollbackAsync(ct);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            InventoryMetrics.RecordReservation(false);
            logger.LogError(ex,
                "Failed to reserve inventory for Order {OrderId}", cmd.OrderId);
            throw;
        }

        if (duplicate)
        {
            logger.LogWarning(
                "Duplicate ReserveInventoryCommand for Order {OrderId} — already reserved, re-confirming",
                cmd.OrderId);
            await context.Publish(new InventoryReservedEvent { OrderId = cmd.OrderId });
            return;
        }

        if (failureReason is not null)
        {
            logger.LogError(
                "Cannot reserve inventory for Order {OrderId}: {Reason}", cmd.OrderId, failureReason);
            InventoryMetrics.RecordReservation(false);
            await context.Publish(new InventoryReservationFailedEvent
            {
                OrderId = cmd.OrderId,
                Reason = failureReason
            });
            return;
        }

        InventoryMetrics.RecordReservation(true);

        // Invalidate inventory cache — reserved quantities changed
        foreach (var line in lines)
            await cache.InvalidateAsync(CacheKeys.Inventory(line.ProductId));
        await cache.InvalidateAsync(CacheKeys.InventoryAll);

        logger.LogInformation("Inventory reserved for Order {OrderId}", cmd.OrderId);
        await context.Publish(new InventoryReservedEvent { OrderId = cmd.OrderId });
    }

    // Returns a human-readable reason if the order cannot be reserved, otherwise null.
    private static string? FindProblem(List<Line> lines, List<InventoryItem> items)
    {
        foreach (var line in lines)
        {
            if (line.Quantity <= 0)
                return $"Invalid quantity for {line.ProductName}";

            var item = items.FirstOrDefault(i => i.ProductId == line.ProductId);
            if (item is null)
                return $"Unknown product {line.ProductName}";

            if (item.AvailableQuantity < line.Quantity)
                return $"Insufficient stock for {line.ProductName}";
        }
        return null;
    }
}
