using Microsoft.EntityFrameworkCore;
using ShopEasy.OrderService.ReadModels;

namespace ShopEasy.OrderService.Data;

public enum SagaStatus
{
    Started,
    ReservingInventory,
    ProcessingPayment,
    Completing,
    Completed,
    ReleasingInventory,
    Cancelled,
    Failed
}

public class OrderSagaState
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public SagaStatus Status { get; set; }
    public string UserId { get; set; } = default!;
    public string UserEmail { get; set; } = default!;
    public decimal TotalAmount { get; set; }
    public string ItemsJson { get; set; } = default!; // serialized items
    public string? FailureReason { get; set; }
    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}


public enum OrderStatus
{
    Pending,
    Confirmed,
    PaymentFailed,
    Shipped,
    Delivered,
    Cancelled
}

public class OutboxEntry
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = default!;
    public string Payload { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public int RetryCount { get; set; }
}

public class Order
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;
    public string UserEmail { get; set; } = default!;
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public decimal TotalAmount { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<OrderItem> Items { get; set; } = new();
}

public class OrderItem
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = default!;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal => Quantity * UnitPrice;
}

public class OrderDbContext : DbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OutboxEntry> OutboxEntries => Set<OutboxEntry>();
    public DbSet<OrderSagaState> SagaStates => Set<OrderSagaState>();
    public DbSet<OrderSummary> OrderSummaries => Set<OrderSummary>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderSummary>(e =>
        {
            e.HasKey(o => o.OrderId);
            e.HasIndex(o => o.UserId);       // fast lookup by user
            e.HasIndex(o => o.StatusCode);   // fast filter by status
            e.HasIndex(o => o.PlacedAt);     // fast sort by date
            e.Property(o => o.ItemsSummary).HasMaxLength(500);
        });

        modelBuilder.Entity<Order>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.TotalAmount).HasColumnType("decimal(18,2)");
            e.HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId);
        });

        modelBuilder.Entity<OrderItem>(e =>
        {
            e.HasKey(i => i.Id);
            e.Property(i => i.UnitPrice).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<OutboxEntry>(e =>
        {
            e.HasKey(o => o.Id);
            e.HasIndex(o => o.DeliveredAt); // fast query for pending
        });

        modelBuilder.Entity<OrderSagaState>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.OrderId).IsUnique();
            e.HasIndex(s => s.Status);
            e.Property(s => s.TotalAmount).HasColumnType("decimal(18,2)");
        });
    }
}