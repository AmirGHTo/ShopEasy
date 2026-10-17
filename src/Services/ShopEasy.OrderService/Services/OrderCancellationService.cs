using MassTransit;
using Microsoft.EntityFrameworkCore;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Metrics;
using ShopEasy.OrderService.ReadModels;
using ShopEasy.OrderService.Sagas;
using ShopEasy.Shared.Contracts;
using System.Text.Json;

namespace ShopEasy.OrderService.Services;

public enum CancelOutcome
{
    Cancelled,          // cancelled right away, nothing was reserved or charged yet
    Cancelling,         // accepted: the saga is releasing stock / the payment will be refunded
    AlreadyCancelled,   // nothing to do — repeating a cancel is safe
    NotFound,           // no such order (or it belongs to someone else)
    NotCancellable      // payment was taken and the order is being/has been completed
}

public record CancelResult(CancelOutcome Outcome, string? Message = null);

// Cancels an order THROUGH the saga, so stock and money are handled by the same state
// machine that created them — never by just flipping the order's status.
//
// The saga row is locked first, so a cancel cannot interleave with the orchestrator, a
// payment result or the timeout service. What happens depends on how far the saga got:
//
//   no saga yet (outbox event in flight)  → record a Cancelled saga so the starter skips it → Cancelled
//   Started                               → nothing reserved or charged yet               → Cancelled
//   ReservingInventory / ProcessingPayment→ saga moves to ReleasingInventory and a
//                                           ReleaseInventoryCommand is sent. A payment that still
//                                           succeeds is refunded by the late-payment guard    → Cancelling
//   ReleasingInventory                    → compensation already running                      → Cancelling
//   Cancelled / Failed                    → already over                                      → AlreadyCancelled
//   Completing / Completed                → payment taken: too late to cancel                 → NotCancellable
public class OrderCancellationService(
    OrderDbContext db,
    ISagaLocking sagaLocking,
    IPublishEndpoint publishEndpoint,
    OrderReadModelUpdater readModelUpdater,
    ILogger<OrderCancellationService> logger)
{
    private const int MaxAttempts = 3;

    public async Task<CancelResult> CancelAsync(
        Guid orderId, string requesterId, bool isAdmin, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var (result, releaseInventory) =
                    await TryCancelAsync(orderId, requesterId, isAdmin, ct);

                if (releaseInventory)
                    await PublishReleaseAsync(orderId, ct);

                return result;
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts)
            {
                // The unique index on SagaStates.OrderId rejected our insert: the saga starter
                // created the saga at the same moment. Start over — the saga exists now.
                logger.LogWarning(ex,
                    "Concurrent saga creation while cancelling Order {OrderId} (attempt {Attempt}) — retrying",
                    orderId, attempt);
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<(CancelResult Result, bool ReleaseInventory)> TryCancelAsync(
        Guid orderId, string requesterId, bool isAdmin, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var saga = await sagaLocking.LockByOrderIdAsync(db, orderId, ct);
        var order = await db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        // Other customers' orders look like they do not exist
        if (order is null || (!isAdmin && order.UserId != requesterId))
            return (new CancelResult(CancelOutcome.NotFound), false);

        var reason = order.UserId == requesterId ? FailureReasons.CancelledByCustomer : FailureReasons.CancelledByAdmin;
        var now = DateTime.UtcNow;

        if (order.Status == OrderStatus.Cancelled)
            return (new CancelResult(CancelOutcome.AlreadyCancelled), false);

        if (order.Status is OrderStatus.Confirmed or OrderStatus.Shipped or OrderStatus.Delivered)
            return (NotCancellable("The order is already confirmed and can no longer be cancelled."), false);

        // ── The saga has not been created yet (the outbox event is still in flight) ──
        if (saga is null)
        {
            // Record a Cancelled saga: OrderConfirmedSagaStarter skips orders that already
            // have one, so this order will never reserve stock or take a payment.
            db.SagaStates.Add(new OrderSagaState
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                Status = SagaStatus.Cancelled,
                UserId = order.UserId,
                UserEmail = order.UserEmail,
                TotalAmount = order.TotalAmount,
                ItemsJson = JsonSerializer.Serialize(order.Items.Select(i => new OrderLineItem
                {
                    ProductId = i.ProductId,
                    ProductName = i.ProductName,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice
                }).ToList()),
                FailureReason = reason,
                CreatedAt = now,
                UpdatedAt = now
            });

            await MarkCancelledAsync(order, reason, now, ct);
            await transaction.CommitAsync(ct);

            OrderMetrics.RecordOrderCancelled(FailureReasons.CancelReasonOf(reason));
            logger.LogInformation(
                "Order {OrderId} cancelled before its saga started ({Reason})", orderId, reason);
            return (new CancelResult(CancelOutcome.Cancelled), false);
        }

        switch (saga.Status)
        {
            case SagaStatus.Completed:
                return (NotCancellable("The order has already been completed and can no longer be cancelled."), false);

            case SagaStatus.Completing:
                return (NotCancellable(
                    "Payment was already taken and the order is being finalized; " +
                    "it can no longer be cancelled."), false);

            case SagaStatus.Cancelled:
            case SagaStatus.Failed:
                // The saga is already over; make sure the order says so, then report success
                await MarkCancelledAsync(order, saga.FailureReason ?? reason, now, ct);
                await transaction.CommitAsync(ct);
                return (new CancelResult(CancelOutcome.AlreadyCancelled), false);

            case SagaStatus.ReleasingInventory:
                return (new CancelResult(CancelOutcome.Cancelling,
                    "The order is already being cancelled."), false);

            case SagaStatus.Started:
                // Nothing has been reserved or charged yet: cancel on the spot
                saga.Status = SagaStatus.Cancelled;
                saga.FailureReason = reason;
                saga.UpdatedAt = now;
                await MarkCancelledAsync(order, reason, now, ct);
                await transaction.CommitAsync(ct);

                OrderMetrics.RecordSagaCancelled(now - saga.CreatedAt);
                OrderMetrics.RecordOrderCancelled(FailureReasons.CancelReasonOf(reason));
                logger.LogInformation(
                    "Order {OrderId} cancelled before any stock was reserved ({Reason})", orderId, reason);
                return (new CancelResult(CancelOutcome.Cancelled), false);

            case SagaStatus.ReservingInventory:
            case SagaStatus.ProcessingPayment:
                // Stock is (or is about to be) reserved and a payment may be in flight. Hand the
                // order to the saga's compensation path: release the stock, and refund a payment
                // that still succeeds (PaymentSucceededConsumer refunds payments that arrive for
                // a ReleasingInventory saga). InventoryReleasedConsumer then cancels the order.
                var previousStatus = saga.Status;
                saga.Status = SagaStatus.ReleasingInventory;
                saga.FailureReason = reason;
                saga.UpdatedAt = now;
                await db.SaveChangesAsync(ct);

                await readModelUpdater.UpdateStatusAsync(
                    order.Id, OrderStatus.Pending, failureReason: reason);
                await transaction.CommitAsync(ct);

                logger.LogInformation(
                    "Order {OrderId} cancellation accepted while saga was {Status} — releasing stock ({Reason})",
                    orderId, previousStatus, reason);
                return (new CancelResult(CancelOutcome.Cancelling,
                    "Cancellation accepted. Reserved stock is being released and any payment will be refunded."),
                    true);

            default:
                return (NotCancellable($"The order cannot be cancelled in its current state ({saga.Status})."), false);
        }
    }

    private async Task MarkCancelledAsync(
        Order order, string reason, DateTime now, CancellationToken ct)
    {
        order.Status = OrderStatus.Cancelled;
        order.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        await readModelUpdater.UpdateStatusAsync(
            order.Id, OrderStatus.Cancelled, failureReason: reason);
    }

    private async Task PublishReleaseAsync(Guid orderId, CancellationToken ct)
    {
        try
        {
            await publishEndpoint.Publish(new ReleaseInventoryCommand { OrderId = orderId }, ct);
        }
        catch (Exception ex)
        {
            // The cancellation is already saved. If the command was lost, SagaTimeoutService
            // re-sends it (release is idempotent), so the caller still gets "accepted".
            logger.LogError(ex,
                "Could not publish ReleaseInventoryCommand for Order {OrderId}; " +
                "the saga timeout service will retry", orderId);
        }
    }

    private static CancelResult NotCancellable(string message) =>
        new(CancelOutcome.NotCancellable, message);
}
