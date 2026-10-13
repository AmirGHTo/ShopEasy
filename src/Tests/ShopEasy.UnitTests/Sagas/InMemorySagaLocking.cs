using Microsoft.EntityFrameworkCore;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Sagas;

namespace ShopEasy.UnitTests.Sagas;

// The real PostgresSagaLocking issues SELECT ... FOR UPDATE, which the InMemory provider
// cannot run. This stand-in loads the same row without locking, so the saga handlers'
// business logic can be unit tested. The locking itself needs a real PostgreSQL.
public sealed class InMemorySagaLocking : ISagaLocking
{
    public Task<OrderSagaState?> LockByOrderIdAsync(
        OrderDbContext db, Guid orderId, CancellationToken ct = default)
        => db.SagaStates.FirstOrDefaultAsync(s => s.OrderId == orderId, ct);
}
