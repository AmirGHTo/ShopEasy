using Microsoft.EntityFrameworkCore;
using ShopEasy.OrderService.Data;

namespace ShopEasy.OrderService.Sagas;

// Row-level locking for saga state transitions.
//
// Several actors change a saga: the event consumers (inventory / payment results), the
// orchestrator that starts it, the timeout service, and order cancellation. Without a lock
// two of them can read the same state and both act on it (for example a payment result
// racing the timeout, or a cancel racing the orchestrator), and the last writer silently
// wins. Every transition therefore loads the saga through this interface inside a database
// transaction: PostgreSQL holds a write lock on the row until the transaction ends, so the
// second actor waits and then sees the first one's outcome.
public interface ISagaLocking
{
    // Loads the saga of an order with SELECT ... FOR UPDATE, with current values from the
    // database. Returns null if the order has no saga yet. Must be called inside a transaction.
    Task<OrderSagaState?> LockByOrderIdAsync(
        OrderDbContext db, Guid orderId, CancellationToken ct = default);
}

public class PostgresSagaLocking : ISagaLocking
{
    public async Task<OrderSagaState?> LockByOrderIdAsync(
        OrderDbContext db, Guid orderId, CancellationToken ct = default)
    {
        // OrderId has a unique index, so there is at most one row.
        var saga = (await db.SagaStates
                .FromSqlInterpolated($"""
                    SELECT * FROM "SagaStates"
                    WHERE "OrderId" = {orderId}
                    FOR UPDATE
                    """)
                .ToListAsync(ct))
            .FirstOrDefault();

        // If this context had already loaded the saga (the orchestrator does), EF returns that
        // tracked instance with its OLD values. Refresh it now that we hold the lock.
        if (saga is not null)
            await db.Entry(saga).ReloadAsync(ct);

        return saga;
    }
}
