using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using ShopEasy.OrderService.Metrics;
using ShopEasy.Shared.Messaging;

namespace ShopEasy.OrderService.Data;

// One row for every change of a saga's status, never updated and never deleted except by the
// retention clean-up. OrderSagaState only says where an order is now; this says where it has been,
// when, for how long and why, which is what an incident review needs and logs and traces (which
// expire) cannot give.
public class SagaTransition
{
    public long Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid SagaId { get; set; }

    // Null for the first row of a saga
    public SagaStatus? FromStatus { get; set; }
    public SagaStatus ToStatus { get; set; }

    public DateTime At { get; set; }

    // How long the saga stayed in FromStatus. Null for the first row.
    public double? SecondsInPreviousStatus { get; set; }

    // The saga's FailureReason when the change was made. It is set when a step fails or the order is
    // cancelled and stays on the saga, so every later row of that compensation path shows it too
    // ("Payment declined" on ReleasingInventory and on Cancelled).
    public string? Reason { get; set; }
    public string? CorrelationId { get; set; }

    public const int MaxReasonLength = 500;
    public const int MaxCorrelationIdLength = 128;
}

// Settings under "SagaHistory" (appsettings.json, or SagaHistory__RetentionDays and so on).
public class SagaHistoryOptions
{
    public const string SectionName = "SagaHistory";

    // How long the rows are kept. An order is settled within minutes; the history is for the days
    // after an incident, so a month is generous and still a small table.
    public int RetentionDays { get; set; } = 30;
}

// The one place where status changes are written down. OrderDbContext calls it from SaveChanges,
// so whatever changes OrderSagaState.Status (a consumer, the orchestrator, the timeout service, the
// cancellation service, a test) leaves its row in the same SaveChanges, hence in the same transaction
// as the change. There is no second place to remember to call.
public static class SagaHistory
{
    // Adds a SagaTransition for every saga that is new or whose status differs from the one it was
    // loaded with, and stamps StatusChangedAt on it. Returns the rows it added.
    internal static List<SagaTransition> Record(OrderDbContext db)
    {
        var now = DateTime.UtcNow;
        var added = new List<SagaTransition>();

        foreach (var entry in db.ChangeTracker.Entries<OrderSagaState>().ToList())
        {
            var saga = entry.Entity;
            SagaStatus? from = null;
            double? seconds = null;

            if (entry.State == EntityState.Modified)
            {
                from = entry.OriginalValues.GetValue<SagaStatus>(nameof(OrderSagaState.Status));
                if (from == saga.Status) continue;

                // A saga from before the history existed has no StatusChangedAt: its last update is the
                // best guess for when it entered its status
                var since = entry.OriginalValues.GetValue<DateTime?>(nameof(OrderSagaState.StatusChangedAt))
                            ?? entry.OriginalValues.GetValue<DateTime>(nameof(OrderSagaState.UpdatedAt));
                seconds = Math.Max(0, (now - since).TotalSeconds);
            }
            else if (entry.State != EntityState.Added)
            {
                continue;
            }

            saga.StatusChangedAt = now;
            var transition = new SagaTransition
            {
                OrderId = saga.OrderId,
                SagaId = saga.Id,
                FromStatus = from,
                ToStatus = saga.Status,
                At = now,
                SecondsInPreviousStatus = seconds,
                Reason = Truncate(saga.FailureReason, SagaTransition.MaxReasonLength),
                CorrelationId = Truncate(saga.CorrelationId ?? CorrelationContext.Current, SagaTransition.MaxCorrelationIdLength)
            };
            db.Add(transition);
            added.Add(transition);
        }

        return added;
    }

    // The save failed: take the rows back out of the change tracker, so that a retry on the same
    // context records them once and not twice
    internal static void Forget(OrderDbContext db, List<SagaTransition> rows)
    {
        foreach (var row in rows)
            db.Entry(row).State = EntityState.Detached;
    }

    // The save succeeded: measure how long each step took
    internal static void Observe(List<SagaTransition> rows)
    {
        foreach (var row in rows)
            if (row.FromStatus is { } from && row.SecondsInPreviousStatus is { } seconds)
                OrderMetrics.RecordSagaStep(from.ToString(), row.ToStatus.ToString(), seconds);
    }

    // Returns how many rows were removed. A single set-based DELETE, so several Order instances
    // can run it at the same time.
    public static Task<int> PurgeAsync(OrderDbContext db, TimeSpan retention, DateTime now, CancellationToken ct = default) =>
        db.SagaTransitions.Where(t => t.At < now - retention).ExecuteDeleteAsync(ct);

    private static string? Truncate(string? value, int max) =>
        value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}
