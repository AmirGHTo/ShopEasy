using Microsoft.EntityFrameworkCore;

namespace ShopEasy.InventoryService.Data;

// Row-level locking for stock changes.
//
// Every code path that changes InventoryItem quantities (reserve, commit, release, admin
// adjust) loads the rows through this interface inside a database transaction. PostgreSQL
// then holds a write lock on those rows until the transaction ends, so two instances of the
// Inventory service can never read the same stock level and both "win" (overselling).
public interface IInventoryLocking
{
    // Loads the given products (tracked) with SELECT ... FOR UPDATE. Must be called inside a
    // transaction. Rows are locked in ProductId order so concurrent callers cannot deadlock.
    Task<List<InventoryItem>> LockItemsAsync(
        InventoryDbContext db,
        IReadOnlyCollection<Guid> productIds,
        CancellationToken ct = default);
}

public class PostgresInventoryLocking : IInventoryLocking
{
    public Task<List<InventoryItem>> LockItemsAsync(
        InventoryDbContext db,
        IReadOnlyCollection<Guid> productIds,
        CancellationToken ct = default)
    {
        var ids = productIds.Distinct().ToArray();

        return db.InventoryItems
            .FromSqlInterpolated($"""
                SELECT * FROM "InventoryItems"
                WHERE "ProductId" = ANY({ids})
                ORDER BY "ProductId"
                FOR UPDATE
                """)
            .ToListAsync(ct);
    }
}
