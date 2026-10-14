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
        // ── MassTransit transactional inbox/outbox tables ────────────────────
        // The consumer endpoints (Program.cs) store the PaymentSucceededEvent / PaymentFailedEvent
        // they publish here, in the same transaction as the payment row, and record every
        // consumed message so a redelivered one is not processed twice. The event is therefore
        // never lost - nor sent for a payment that was rolled back - even if RabbitMQ is down.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        modelBuilder.Entity<ProcessedEvent>(e =>
        {
            e.HasKey(p => p.EventId);
        });
    }
}