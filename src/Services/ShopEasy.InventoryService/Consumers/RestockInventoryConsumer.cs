using MassTransit;
using Microsoft.EntityFrameworkCore;
using ShopEasy.InventoryService.Data;
using ShopEasy.Shared.Contracts;
using ShopEasy.Shared.Services;

namespace ShopEasy.InventoryService.Consumers;

// Compensation for an order that was paid, committed and then cancelled: the units the commit
// took off the shelf are added back and the saga is told (InventoryRestockedEvent).
// Idempotent, decided from the order's ledger rows under the row locks:
//   committed, not restocked yet → add the quantities back, write the ledger rows
//   already restocked / nothing reserved → confirm again, change nothing
//   not committed → refuse and publish nothing: adding units that were never taken off the
//                   shelf would create stock that does not exist (the saga's timeout flags it)
// Transient errors throw, so MassTransit retries the message.
public class RestockInventoryConsumer(
    InventoryDbContext db,
    IInventoryLocking locking,
    CacheService cache,
    ILogger<RestockInventoryConsumer> logger) : IConsumer<RestockInventoryCommand>
{
    private enum Outcome { NothingToRestock, AlreadyRestocked, NotCommitted, Restocked }

    public async Task Consume(ConsumeContext<RestockInventoryCommand> context)
    {
        var orderId = context.Message.OrderId;
        var ct = context.CancellationToken;
        logger.LogInformation("Putting the stock of cancelled Order {OrderId} back", orderId);

        Outcome outcome;
        var affectedProductIds = new List<Guid>();

        await using var transaction = await db.Database.BeginOrJoinAsync(ct);
        try
        {
            // unlocked read — only decides what to lock
            var reservedReason = StockReasons.Reserved(orderId);
            var productIds = await db.StockTransactions.AsNoTracking()
                .Where(t => t.OrderId == orderId && t.Reason == reservedReason)
                .Select(t => t.ProductId)
                .Distinct()
                .ToListAsync(ct);

            if (productIds.Count == 0)
            {
                outcome = Outcome.NothingToRestock;
            }
            else
            {
                var items = await locking.LockItemsAsync(db, productIds, ct);
                var rows = await db.StockTransactions.AsNoTracking()
                    .Where(t => t.OrderId == orderId)
                    .ToListAsync(ct);
                var state = OrderStockState.From(orderId, rows);

                if (state.FullyRestocked)
                {
                    outcome = Outcome.AlreadyRestocked;
                }
                else if (!state.FullyCommitted)
                {
                    outcome = Outcome.NotCommitted;
                }
                else
                {
                    var now = DateTime.UtcNow;
                    foreach (var (productId, qty) in state.ReservedQuantity
                                 .Where(r => !state.Restocked.Contains(r.Key)))
                    {
                        var item = items.FirstOrDefault(i => i.ProductId == productId);
                        if (item is null)
                        {
                            logger.LogError(
                                "Inventory record for Product {ProductId} is missing while restocking " +
                                "Order {OrderId}. DATA INCONSISTENCY, investigate.", productId, orderId);
                        }
                        else
                        {
                            item.Quantity += qty;
                            item.UpdatedAt = now;
                            affectedProductIds.Add(productId);
                        }

                        db.StockTransactions.Add(new StockTransaction
                        {
                            Id = Guid.NewGuid(),
                            ProductId = productId,
                            QuantityChange = item is null ? 0 : qty,
                            Reason = StockReasons.Restocked(orderId),
                            OrderId = orderId,
                            CreatedAt = now
                        });
                    }

                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    outcome = Outcome.Restocked;
                }
            }

            if (outcome != Outcome.Restocked)
                await transaction.RollbackAsync(ct);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            logger.LogError(ex, "Failed to restock Order {OrderId}", orderId);
            throw;
        }

        if (outcome == Outcome.NotCommitted)
        {
            logger.LogError(
                "Order {OrderId} was never committed, so there is no stock to put back. " +
                "Ignoring RestockInventoryCommand.", orderId);
            return;
        }

        if (outcome == Outcome.Restocked)
        {
            foreach (var productId in affectedProductIds)
                await cache.InvalidateAsync(CacheKeys.Inventory(productId));
            await cache.InvalidateAsync(CacheKeys.InventoryAll);
            logger.LogInformation("Stock of Order {OrderId} is back", orderId);
        }

        await context.Publish(new InventoryRestockedEvent { OrderId = orderId });
    }
}
