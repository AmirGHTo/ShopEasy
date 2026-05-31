using MassTransit;
using Microsoft.EntityFrameworkCore;
using ShopEasy.Shared.Contracts;

namespace ShopEasy.PaymentService.Data;

public class ProcessedEvent
{
    public Guid EventId { get; set; } // OrderId used as the idempotency key
    public string EventType { get; set; } = default!;
    public DateTime ProcessedAt { get; set; }
}
public class Payment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string UserId { get; set; } = default!;
    public decimal Amount { get; set; }
    public PaymentStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public string Provider { get; set; } = "simulated";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class PaymentDbContext : DbContext
{
    public PaymentDbContext(DbContextOptions<PaymentDbContext> options) : base(options) { }

    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── MassTransit Outbox tables ────────────────────────────────────────
        // PaymentService publishes PaymentProcessedEvent after processing.
        // The Outbox ensures this event is never lost even if RabbitMQ is down.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        modelBuilder.Entity<ProcessedEvent>(e =>
        {
            e.HasKey(p => p.EventId);
        });
    }
}