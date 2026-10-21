using MassTransit;
using Microsoft.EntityFrameworkCore;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Metrics;
using ShopEasy.OrderService.ReadModels;
using ShopEasy.Shared.Contracts;

namespace ShopEasy.OrderService.Sagas;

// Saga event handlers. Conventions used by all of them:
//  * The saga row is locked (ISagaLocking) inside a transaction before it is read, so two
//    actors changing the same saga are serialized and the second sees the first one's result.
//  * A handler only acts when the saga is in the state its event is valid for. Anything else
//    is a duplicate (at-least-once delivery) or a late message and is ignored or compensated.
//  * A handler that sends the next command publishes it INSIDE the transaction. Its endpoint
//    uses the MassTransit outbox (Program.cs), so the command is stored in the OutboxMessage
//    table together with the new state and sent to RabbitMQ after the commit: a crash or a
//    broker outage after the commit cannot lose it. The handler joins MassTransit's transaction
//    (BeginOrJoinAsync); without the outbox (unit tests) it opens its own.

// ── Inventory reserved → move to payment ─────────────────────────────────────
public class InventoryReservedConsumer(
    OrderDbContext db,
    ISagaLocking sagaLocking,
    IPublishEndpoint publishEndpoint,
    ILogger<InventoryReservedConsumer> logger): IConsumer<InventoryReservedEvent>
{
    public async Task Consume(ConsumeContext<InventoryReservedEvent> context)
    {
        var evt = context.Message;

        await using (var transaction = await db.Database.BeginOrJoinAsync(context.CancellationToken))
        {
            try
            {
                var saga = await sagaLocking.LockByOrderIdAsync(db, evt.OrderId);
                if (saga is null) return;

                // Zombie guard — the saga already gave up on this order (or is cancelling it),
                // so this late reservation must be given back.
                if (saga.Status == SagaStatus.Cancelled ||
                    saga.Status == SagaStatus.Failed ||
                    saga.Status == SagaStatus.ReleasingInventory)
                {
                    logger.LogWarning(
                        "Saga {SagaId}: received InventoryReservedEvent for Order {OrderId} " +
                        "but saga is in state {Status} — releasing late reservation.",
                        saga.Id, evt.OrderId, saga.Status);
                    await publishEndpoint.Publish(new ReleaseInventoryCommand
                    {
                        OrderId = evt.OrderId
                    }, context.CancellationToken);
                }
                // ── Idempotency guard ─────────────────────────────────────────
                // If saga already moved past ReservingInventory (including Completed — its
                // reservation is the committed one and must NOT be released), this is a
                // duplicate event — skip silently.
                else if (saga.Status != SagaStatus.ReservingInventory)
                {
                    logger.LogWarning(
                        "Saga {SagaId}: received duplicate InventoryReservedEvent " +
                        "but status is already {Status} — skipping.",
                        saga.Id, saga.Status);
                }
                else
                {
                    saga.Status = SagaStatus.ProcessingPayment;
                    saga.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();

                    logger.LogInformation(
                        "Saga for Order {OrderId}: inventory reserved → sending ProcessPaymentCommand",
                        evt.OrderId);
                    await publishEndpoint.Publish(new ProcessPaymentCommand
                    {
                        OrderId = saga.OrderId,
                        UserId = saga.UserId,
                        UserEmail = saga.UserEmail,
                        Amount = saga.TotalAmount,
                        Currency = "USD"
                    }, context.CancellationToken);
                    await transaction.CommitAsync();
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                logger.LogError(ex,
                    "Failed to process InventoryReservedEvent for Order {OrderId}",
                    evt.OrderId);
                throw;
            }
        }

    }
}

// ── Inventory reservation failed → cancel saga ────────────────────────────────
public class InventoryReservationFailedConsumer(
    OrderDbContext db,
    ISagaLocking sagaLocking,
    OrderReadModelUpdater readModelUpdater,
    ILogger<InventoryReservationFailedConsumer> logger)
    : IConsumer<InventoryReservationFailedEvent>
{
    public async Task Consume(ConsumeContext<InventoryReservationFailedEvent> context)
    {
        var evt = context.Message;

        await using var transaction = await db.Database.BeginTransactionAsync();

        var saga = await sagaLocking.LockByOrderIdAsync(db, evt.OrderId);
        if (saga is null) return;

        // Started: the reply can overtake the orchestrator's own state update
        if (saga.Status != SagaStatus.ReservingInventory &&
            saga.Status != SagaStatus.Started)
        {
            logger.LogWarning(
                "Saga {SagaId}: ignoring InventoryReservationFailedEvent in state {Status}",
                saga.Id, saga.Status);
            return;
        }

        logger.LogWarning(
            "Saga {SagaId}: Inventory reservation failed — {Reason}. Cancelling.",
            saga.Id, evt.Reason);

        // Update order status to Cancelled
        var order = await db.Orders.FindAsync(saga.OrderId);
        if (order is not null)
        {
            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = DateTime.UtcNow;
        }

        saga.Status = SagaStatus.Cancelled;
        saga.FailureReason = evt.Reason;
        saga.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await readModelUpdater.UpdateStatusAsync(
            saga.OrderId, OrderStatus.Cancelled, failureReason: evt.Reason);
        await transaction.CommitAsync();

        OrderMetrics.RecordSagaCancelled(DateTime.UtcNow - saga.CreatedAt);
        OrderMetrics.RecordOrderCancelled(CancelReason.StockUnavailable);
    }
}

// ── Payment succeeded → commit the reservation ────────────────────────────────
public class PaymentSucceededConsumer(
    OrderDbContext db,
    ISagaLocking sagaLocking,
    IPublishEndpoint publishEndpoint,
    OrderReadModelUpdater readModelUpdater,
    ILogger<PaymentSucceededConsumer> logger)
    : IConsumer<PaymentSucceededEvent>
{
    public async Task Consume(ConsumeContext<PaymentSucceededEvent> context)
    {
        var evt = context.Message;

        await using (var transaction = await db.Database.BeginOrJoinAsync(context.CancellationToken))
        {
            try
            {
                var saga = await sagaLocking.LockByOrderIdAsync(db, evt.OrderId);
                if (saga is null) return;

                if (saga.Status == SagaStatus.Completing ||
                    saga.Status == SagaStatus.Completed)
                {
                    // Duplicate delivery — payment was already handled. This must NOT trigger
                    // a refund: the customer paid for an order that is completing/completed.
                    logger.LogWarning(
                        "Saga {SagaId}: duplicate PaymentSucceededEvent for Order {OrderId} " +
                        "(saga already {Status}) — ignoring.",
                        saga.Id, evt.OrderId, saga.Status);
                }
                else if (saga.Status == SagaStatus.Cancelled ||
                         saga.Status == SagaStatus.Failed ||
                         saga.Status == SagaStatus.ReleasingInventory)
                {
                    // Late payment — the saga already gave up on this order (timeout or the
                    // customer cancelled), so refund the customer
                    logger.LogWarning(
                        "Saga {SagaId}: received PaymentSucceededEvent for Order {OrderId} " +
                        "but saga is in state {Status} — issuing refund for late payment.",
                        saga.Id, evt.OrderId, saga.Status);

                    await publishEndpoint.Publish(new RefundPaymentCommand
                    {
                        OrderId = saga.OrderId,
                        Reason = $"Order was already in state {saga.Status} " +
                                  $"when payment arrived"
                    }, context.CancellationToken);
                }
                else
                {
                    saga.Status = SagaStatus.Completing;
                    saga.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();

                    await readModelUpdater.UpdateStatusAsync(
                        evt.OrderId,
                        OrderStatus.Pending,
                        paymentStatus: "Succeeded");

                    // The order is only Completed once InventoryCommittedEvent arrives
                    // (InventoryCommittedConsumer). SagaTimeoutService re-sends the (idempotent)
                    // commit command if that never happens.
                    logger.LogInformation(
                        "Saga for Order {OrderId}: payment succeeded → committing inventory",
                        evt.OrderId);
                    await publishEndpoint.Publish(
                        new CommitInventoryCommand { OrderId = saga.OrderId }, context.CancellationToken);
                    await transaction.CommitAsync();
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                logger.LogError(ex,
                    "Failed to process PaymentSucceededEvent for Order {OrderId}",
                    evt.OrderId);
                throw;
            }
        }

    }
}

// ── Inventory committed → order is complete ───────────────────────────────────
public class InventoryCommittedConsumer(
    OrderDbContext db,
    ISagaLocking sagaLocking,
    OrderReadModelUpdater readModelUpdater,
    ILogger<InventoryCommittedConsumer> logger)
    : IConsumer<InventoryCommittedEvent>
{
    public async Task Consume(ConsumeContext<InventoryCommittedEvent> context)
    {
        var evt = context.Message;

        await using var transaction = await db.Database.BeginOrJoinAsync(context.CancellationToken);

        var saga = await sagaLocking.LockByOrderIdAsync(db, evt.OrderId);
        if (saga is null) return;

        if (saga.Status == SagaStatus.Completed)
        {
            logger.LogWarning(
                "Saga {SagaId}: duplicate InventoryCommittedEvent — already completed",
                saga.Id);
            return;
        }

        if (saga.Status != SagaStatus.Completing)
        {
            // Stock was deducted and the customer was charged, but the saga had already
            // moved to a cancelling/failed state. Do not guess: needs a human.
            logger.LogCritical(
                "Saga {SagaId}: inventory was committed for Order {OrderId} but the saga is in " +
                "state {Status}. Payment was taken and stock deducted — MANUAL INTERVENTION REQUIRED.",
                saga.Id, evt.OrderId, saga.Status);
            return;
        }

        // Cancelled while the commit was in flight (OrderCancellationService left the request in
        // FailureReason): the stock is committed and the money taken, so the order is not completed.
        // Refund it; the saga then puts the stock back and cancels it.
        if (FailureReasons.IsCancelRequest(saga.FailureReason))
        {
            saga.Status = SagaStatus.RefundingPayment;
            saga.RetryCount = 0;
            saga.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await readModelUpdater.UpdateStatusAsync(
                saga.OrderId, OrderStatus.Pending, paymentStatus: "Refunding");

            logger.LogInformation(
                "Saga {SagaId}: commit done for Order {OrderId}, which was cancelled meanwhile — refunding",
                saga.Id, saga.OrderId);
            await context.Publish(
                new RefundPaymentCommand { OrderId = saga.OrderId, Reason = saga.FailureReason! },
                context.CancellationToken);
            await transaction.CommitAsync();
            return;
        }

        var order = await db.Orders.FindAsync(saga.OrderId);
        if (order is not null)
        {
            order.Status = OrderStatus.Confirmed;
            order.UpdatedAt = DateTime.UtcNow;
        }

        saga.Status = SagaStatus.Completed;
        saga.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await readModelUpdater.UpdateStatusAsync(
            saga.OrderId,
            OrderStatus.Confirmed,
            paymentStatus: "Succeeded");
        await transaction.CommitAsync();

        OrderMetrics.RecordSagaCompleted(DateTime.UtcNow - saga.CreatedAt);
        OrderMetrics.RecordOrderConfirmed(saga.TotalAmount);

        logger.LogInformation(
            "Saga {SagaId}: Order {OrderId} completed successfully ✅",
            saga.Id, saga.OrderId);
    }
}

// ── Inventory commit impossible → refund and cancel ───────────────────────────
// The reservation is missing or was already released, so the order cannot be fulfilled.
// A commit is only ever requested after the payment succeeded, so the customer must be
// refunded whatever state the saga is in.
public class InventoryCommitFailedConsumer(
    OrderDbContext db,
    ISagaLocking sagaLocking,
    IPublishEndpoint publishEndpoint,
    OrderReadModelUpdater readModelUpdater,
    ILogger<InventoryCommitFailedConsumer> logger)
    : IConsumer<InventoryCommitFailedEvent>
{
    public async Task Consume(ConsumeContext<InventoryCommitFailedEvent> context)
    {
        var evt = context.Message;
        var failedSaga = false;

        await using (var transaction = await db.Database.BeginOrJoinAsync(context.CancellationToken))
        {
            var saga = await sagaLocking.LockByOrderIdAsync(db, evt.OrderId);
            if (saga is null) return;

            if (saga.Status == SagaStatus.Completed)
            {
                logger.LogWarning(
                    "Saga {SagaId}: ignoring InventoryCommitFailedEvent — order already completed",
                    saga.Id);
                return;
            }

            if (saga.Status != SagaStatus.Completing)
            {
                // Already cancelling / cancelled / failed — just make sure the customer gets
                // their money back (refunds are idempotent).
                logger.LogWarning(
                    "Saga {SagaId}: inventory commit failed ({Reason}) while saga is {Status} — " +
                    "refunding payment.", saga.Id, evt.Reason, saga.Status);
            }
            else
            {
                logger.LogError(
                    "Saga {SagaId}: inventory commit failed ({Reason}) after payment — " +
                    "refunding and cancelling Order {OrderId}.", saga.Id, evt.Reason, saga.OrderId);

                var order = await db.Orders.FindAsync(saga.OrderId);
                if (order is not null)
                {
                    order.Status = OrderStatus.Cancelled;
                    order.UpdatedAt = DateTime.UtcNow;
                }

                saga.Status = SagaStatus.Failed;
                saga.FailureReason = $"Inventory commit failed: {evt.Reason}";
                saga.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                await readModelUpdater.UpdateStatusAsync(
                    saga.OrderId,
                    OrderStatus.Cancelled,
                    paymentStatus: "Refunding",
                    failureReason: saga.FailureReason);
                failedSaga = true;
            }

            await publishEndpoint.Publish(new RefundPaymentCommand
            {
                OrderId = saga.OrderId,
                Reason = $"Inventory could not be committed: {evt.Reason}"
            }, context.CancellationToken);
            await transaction.CommitAsync();
        }

        if (failedSaga)
        {
            OrderMetrics.RecordSagaFailed();
            OrderMetrics.RecordOrderCancelled(CancelReason.InventoryCommitFailed);
        }
    }
}

// ── Payment failed → release inventory (compensate) ───────────────────────────
public class PaymentFailedConsumer(
    OrderDbContext db,
    ISagaLocking sagaLocking,
    IPublishEndpoint publishEndpoint,
    OrderReadModelUpdater readModelUpdater,
    ILogger<PaymentFailedConsumer> logger)
    : IConsumer<PaymentFailedEvent>
{
    public async Task Consume(ConsumeContext<PaymentFailedEvent> context)
    {
        var evt = context.Message;

        await using (var transaction = await db.Database.BeginOrJoinAsync(context.CancellationToken))
        {
            var saga = await sagaLocking.LockByOrderIdAsync(db, evt.OrderId);
            if (saga is null) return;

            // Only valid while we are waiting for the payment result. Anything else is a
            // duplicate or a late result after the saga already timed out / was cancelled
            // and compensated.
            if (saga.Status != SagaStatus.ProcessingPayment)
            {
                logger.LogWarning(
                    "Saga {SagaId}: ignoring PaymentFailedEvent in state {Status}",
                    saga.Id, saga.Status);
                return;
            }

            logger.LogWarning(
                "Saga {SagaId}: Payment failed ({Reason}) → releasing inventory",
                saga.Id, evt.Reason);

            saga.Status = SagaStatus.ReleasingInventory;
            saga.FailureReason = evt.Reason;
            saga.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            await readModelUpdater.UpdateStatusAsync(
                saga.OrderId, OrderStatus.Pending, failureReason: evt.Reason);

            // Send the compensating command to release the inventory reservation
            await publishEndpoint.Publish(new ReleaseInventoryCommand
            {
                OrderId = evt.OrderId
            }, context.CancellationToken);
            await transaction.CommitAsync();
        }
    }
}

// ── Inventory released → saga cancelled ──────────────────────────────────────
public class InventoryReleasedConsumer(
    OrderDbContext db,
    ISagaLocking sagaLocking,
    OrderReadModelUpdater readModelUpdater,
    ILogger<InventoryReleasedConsumer> logger)
    : IConsumer<InventoryReleasedEvent>
{
    public async Task Consume(ConsumeContext<InventoryReleasedEvent> context)
    {
        var evt = context.Message;

        await using var transaction = await db.Database.BeginTransactionAsync();

        var saga = await sagaLocking.LockByOrderIdAsync(db, evt.OrderId);
        if (saga is null) return;

        // Only the compensation we asked for can cancel the saga. A release confirmation
        // for a saga that is in any other state (e.g. the late-reservation release sent by
        // the zombie guard, or a duplicate) must not touch the order.
        if (saga.Status != SagaStatus.ReleasingInventory)
        {
            logger.LogInformation(
                "Saga {SagaId}: ignoring InventoryReleasedEvent in state {Status}",
                saga.Id, saga.Status);
            return;
        }

        logger.LogInformation(
            "Saga {SagaId}: Inventory released → saga cancelled",
            saga.Id);

        // Update order status to Cancelled
        var order = await db.Orders.FindAsync(saga.OrderId);
        if (order is not null)
        {
            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = DateTime.UtcNow;
        }

        saga.Status = SagaStatus.Cancelled;
        saga.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // After saga moves to Cancelled:
        await readModelUpdater.UpdateStatusAsync(
            saga.OrderId,
            OrderStatus.Cancelled,
            paymentStatus: saga.FailureReason?.Contains("payment") == true
                ? "Failed" : null);
        await transaction.CommitAsync();

        OrderMetrics.RecordSagaCancelled(DateTime.UtcNow - saga.CreatedAt);
        OrderMetrics.RecordOrderCancelled(FailureReasons.CancelReasonOf(saga.FailureReason));
    }
}
