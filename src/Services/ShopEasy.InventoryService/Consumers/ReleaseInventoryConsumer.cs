using MassTransit;
using Microsoft.EntityFrameworkCore;
using ShopEasy.InventoryService.Data;
using ShopEasy.Shared.Contracts;
using ShopEasy.Shared.Services;

namespace ShopEasy.InventoryService.Consumers;

// Compensating action — called when payment fails or the saga gives up.
// Releases the reserved stock back to available.
//
// Idempotent and safe against out-of-order messages (decided from the order's ledger rows,
// under the row locks):
//   nothing reserved             → nothing to release; confirm so the saga can finish
//   already released             → confirm again, no change
//   already COMMITTED            → stock is already deducted; releasing would corrupt the
//                                  counts, so refuse and publish nothing (logged as an error)
public class ReleaseInventoryConsumer(
    InventoryDbContext db,
    IInventoryLocking locking,
    CacheService cache,
    ILogger<ReleaseInventoryConsumer> logger) : IConsumer<ReleaseInventoryCommand>
{
    private enum Outcome { NothingToRelease, AlreadyReleased, AlreadyCommitted, Released }

    public async Task Consume(ConsumeContext<ReleaseInventoryCommand> context)
    {
        var orderId = context.Message.OrderId;
        var ct = context.CancellationToken;
        logger.LogInformation(
            "Releasing inventory reservation for Order {OrderId}", orderId);

        Outcome outcome;
        var affectedProductIds = new List<Guid>();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // 1. Which products did this order reserve? (unlocked read — only decides what to lock)
            var reservedReason = StockReasons.Reserved(orderId);
            var productIds = await db.StockTransactions.AsNoTracking()
                .Where(t => t.OrderId == orderId && t.Reason == reservedReason)
                .Select(t => t.ProductId)
                .Distinct()
                .ToListAsync(ct);

            if (productIds.Count == 0)
            {
                outcome = Outcome.NothingToRelease;
            }
            else
            {
                // 2. Lock the rows, then read the order's ledger state under the lock
                var items = await locking.LockItemsAsync(db, productIds, ct);
                var rows = await db.StockTransactions.AsNoTracking()
                    .Where(t => t.OrderId == orderId)
                    .ToListAsync(ct);
                var state = OrderStockState.From(orderId, rows);

                if (state.AnyCommitted)
                {
                    outcome = Outcome.AlreadyCommitted;
                }
                else if (state.FullyReleased)
                {
                    outcome = Outcome.AlreadyReleased;
                }
                else
                {
                    var now = DateTime.UtcNow;
                    foreach (var (productId, qty) in state.ReservedQuantity
                                 .Where(r => !state.Released.Contains(r.Key)))
                    {
                        var item = items.FirstOrDefault(i => i.ProductId == productId);

                        if (item is null)
                        {
                            // Should never happen: a reservation is only written for an existing
                            // product and nothing deletes inventory records. The order must still be
                            // cancelled, so do not fail the compensation — but do not hide it either.
                            // There are no counts to correct for this product; the Released row below
                            // still records that the order's reservation was dealt with, so a repeat
                            // of this command is recognised as a duplicate.
                            logger.LogError(
                                "Inventory record for Product {ProductId} is missing while releasing " +
                                "Order {OrderId} — nothing to release for it. DATA INCONSISTENCY, " +
                                "investigate.", productId, orderId);
                        }
                        else
                        {
                            item.ReservedQty = Math.Max(0, item.ReservedQty - qty);
                            item.UpdatedAt = now;
                            affectedProductIds.Add(productId);

                            logger.LogInformation(
                                "Released reservation for Product {ProductId} for Order {OrderId}",
                                productId, orderId);
                        }

                        db.StockTransactions.Add(new StockTransaction
                        {
                            Id = Guid.NewGuid(),
                            ProductId = productId,
                            QuantityChange = 0,
                            Reason = StockReasons.Released(orderId),
                            OrderId = orderId,
                            CreatedAt = now
                        });
                    }

                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    outcome = Outcome.Released;
                }
            }

            // Nothing was changed — release the row locks
            if (outcome != Outcome.Released)
                await transaction.RollbackAsync(ct);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            logger.LogError(ex, "Failed to release inventory for Order {OrderId}", orderId);
            throw;
        }

        switch (outcome)
        {
            case Outcome.AlreadyCommitted:
                logger.LogError(
                    "Order {OrderId} was already committed — its reservation cannot be released. " +
                    "Ignoring ReleaseInventoryCommand.", orderId);
                return;

            case Outcome.NothingToRelease:
                logger.LogWarning(
                    "No reservations found for Order {OrderId} — nothing to release", orderId);
                break;

            case Outcome.AlreadyReleased:
                logger.LogWarning(
                    "Reservation for Order {OrderId} was already released — re-confirming", orderId);
                break;

            case Outcome.Released:
                foreach (var productId in affectedProductIds)
                    await cache.InvalidateAsync(CacheKeys.Inventory(productId));
                await cache.InvalidateAsync(CacheKeys.InventoryAll);
                logger.LogInformation("Inventory released for Order {OrderId}", orderId);
                break;
        }

        await context.Publish(new InventoryReleasedEvent { OrderId = orderId });
    }
}
