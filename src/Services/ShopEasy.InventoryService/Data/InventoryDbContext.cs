using Microsoft.EntityFrameworkCore;

namespace ShopEasy.InventoryService.Data;

public class ProcessedEvent
{
    public Guid EventId { get; set; } // OrderId used as the idempotency key
    public string EventType { get; set; } = default!;
    public DateTime ProcessedAt { get; set; }
}

public class InventoryItem
{
    public Guid   ProductId    { get; set; }
    public string ProductName  { get; set; } = default!;
    public int    Quantity     { get; set; }
    public int    ReservedQty  { get; set; } // Reserved in pending orders
    public int    LowStockThreshold { get; set; } = 5;
    public DateTime UpdatedAt  { get; set; }

    // Available = Quantity - ReservedQty
    public int AvailableQuantity => Math.Max(0, Quantity - ReservedQty);
}

public class StockTransaction
{
    public Guid     Id          { get; set; }
    public Guid     ProductId   { get; set; }
    public int      QuantityChange { get; set; } // negative = reduction
    public string   Reason      { get; set; } = default!;
    public Guid?    OrderId     { get; set; }
    public DateTime CreatedAt   { get; set; }
}

public class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<InventoryItem>    InventoryItems    => Set<InventoryItem>();
    public DbSet<StockTransaction> StockTransactions => Set<StockTransaction>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InventoryItem>(e =>
        {
            e.HasKey(i => i.ProductId);
        });

        modelBuilder.Entity<ProcessedEvent>(e =>
        {
            e.HasKey(p => p.EventId);
        });
    }
}

public static class InventoryDbSeeder
{
    // Pre-populate stock for the same products seeded in ProductService
    public static void Seed(InventoryDbContext db)
    {
        if (db.InventoryItems.Any()) return;

        // We'll use fixed GUIDs so OrderService can reference them in tests
        db.InventoryItems.AddRange(
            new InventoryItem { ProductId = Guid.Parse("00000000-0000-0000-0000-000000000001"), ProductName = "Wireless Headphones",    Quantity = 50,  UpdatedAt = DateTime.UtcNow },
            new InventoryItem { ProductId = Guid.Parse("00000000-0000-0000-0000-000000000002"), ProductName = "Mechanical Keyboard",    Quantity = 30,  UpdatedAt = DateTime.UtcNow },
            new InventoryItem { ProductId = Guid.Parse("00000000-0000-0000-0000-000000000003"), ProductName = "Standing Desk",          Quantity = 10,  UpdatedAt = DateTime.UtcNow },
            new InventoryItem { ProductId = Guid.Parse("00000000-0000-0000-0000-000000000004"), ProductName = "Ergonomic Office Chair", Quantity = 15,  UpdatedAt = DateTime.UtcNow },
            new InventoryItem { ProductId = Guid.Parse("00000000-0000-0000-0000-000000000005"), ProductName = "4K Webcam",              Quantity = 100, UpdatedAt = DateTime.UtcNow }
        );
        db.SaveChanges();
    }
}
