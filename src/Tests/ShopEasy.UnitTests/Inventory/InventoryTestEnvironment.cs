using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using MassTransit;
using NSubstitute;
using ShopEasy.InventoryService.Data;
using ShopEasy.Shared.Services;

namespace ShopEasy.UnitTests.Inventory;

// The real PostgresInventoryLocking issues SELECT ... FOR UPDATE, which the InMemory provider
// cannot run. This stand-in loads the same rows without locking, so the consumers' business
// logic (idempotency, state checks, quantity maths) can be unit tested. The locking itself
// needs a real PostgreSQL (Testcontainers) and is covered by the manual concurrency check
// described in the README.
public sealed class InMemoryInventoryLocking : IInventoryLocking
{
    public Task<List<InventoryItem>> LockItemsAsync(
        InventoryDbContext db, IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
        => db.InventoryItems
            .Where(i => productIds.Contains(i.ProductId))
            .OrderBy(i => i.ProductId)
            .ToListAsync(ct);
}

public sealed class InventoryTestEnvironment : IDisposable
{
    public InventoryDbContext Db { get; }
    public IInventoryLocking Locking { get; } = new InMemoryInventoryLocking();
    public CacheService Cache { get; }

    public InventoryTestEnvironment()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // the consumers open transactions, which the InMemory provider ignores
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        Db = new InventoryDbContext(options);
        Cache = new CacheService(
            Substitute.For<IDistributedCache>(), NullLogger<CacheService>.Instance);
    }

    public InventoryItem AddItem(Guid productId, int quantity, int reserved = 0, string name = "Product")
    {
        var item = new InventoryItem
        {
            ProductId = productId,
            ProductName = name,
            Quantity = quantity,
            ReservedQty = reserved,
            UpdatedAt = DateTime.UtcNow
        };
        Db.InventoryItems.Add(item);
        Db.SaveChanges();
        return item;
    }

    // Writes a ledger row the way the consumers do (kind = Reason text)
    public void AddLedger(Guid orderId, Guid productId, string reason, int quantityChange)
    {
        Db.StockTransactions.Add(new StockTransaction
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            QuantityChange = quantityChange,
            Reason = reason,
            OrderId = orderId,
            CreatedAt = DateTime.UtcNow
        });
        Db.SaveChanges();
    }

    public int LedgerCount(Guid orderId, string reason)
        => Db.StockTransactions.Count(t => t.OrderId == orderId && t.Reason == reason);

    public static ConsumeContext<T> Context<T>(T message) where T : class
    {
        var context = Substitute.For<ConsumeContext<T>>();
        context.Message.Returns(message);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    public void Dispose() => Db.Dispose();
}
