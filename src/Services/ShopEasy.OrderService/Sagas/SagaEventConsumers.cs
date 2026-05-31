using MassTransit;
using Microsoft.EntityFrameworkCore;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Metrics;
using ShopEasy.OrderService.ReadModels;
using ShopEasy.Shared.Contracts;

namespace ShopEasy.OrderService.Sagas;

// ── Inventory reserved → move to payment ─────────────────────────────────────
public class InventoryReservedConsumer(
    OrderDbContext db,
    IPublishEndpoint publishEndpoint,
    ILogger<InventoryReservedConsumer> logger): IConsumer<InventoryReservedEvent>
{
    public async Task Consume(ConsumeContext<InventoryReservedEvent> context)
    {
        var evt = context.Message;

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var saga = await db.SagaStates.FirstOrDefaultAsync(s => s.OrderId == evt.OrderId);

            if (saga is null)
            {
                await transaction.RollbackAsync();
                return;
            }

            // Zombie guard — saga already in terminal state
            if (saga.Status == SagaStatus.Cancelled ||
                saga.Status == SagaStatus.Failed ||
                saga.Status == SagaStatus.Completed)
            {
                logger.LogWarning(
                    "Saga {SagaId}: received InventoryReservedEvent for Order {OrderId} " +
                    "but saga is in state {Status} — releasing late reservation.",
                    saga.Id, evt.OrderId, saga.Status);

                await publishEndpoint.Publish(new ReleaseInventoryCommand
                {
                    OrderId = saga.OrderId
                }, context.CancellationToken);

                await transaction.RollbackAsync();
                return;
            }

            // ── Idempotency guard ─────────────────────────────────────────────
            // If saga already moved past ReservingInventory, this is a duplicate
            // event — skip silently. This happens when the message is redelivered
            // after the first successful processing.
            if (saga.Status != SagaStatus.ReservingInventory)
            {
                logger.LogWarning(
                    "Saga {SagaId}: received duplicate InventoryReservedEvent " +
                    "but status is already {Status} — skipping.",
                    saga.Id, saga.Status);

                await transaction.RollbackAsync();
                return;
            }

            logger.LogInformation(
                "Saga {SagaId}: Inventory reserved → sending ProcessPaymentCommand",
                saga.Id);

            await publishEndpoint.Publish(new ProcessPaymentCommand
            {
                OrderId = saga.OrderId,
                UserId = saga.UserId,
                UserEmail = saga.UserEmail,
                Amount = saga.TotalAmount,
                Currency = "USD"
            }, context.CancellationToken);

            saga.Status = SagaStatus.ProcessingPayment;
            saga.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
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

// ── Inventory reservation failed → cancel saga ────────────────────────────────
public class InventoryReservationFailedConsumer(
    OrderDbContext db,
    ILogger<InventoryReservationFailedConsumer> logger)
    : IConsumer<InventoryReservationFailedEvent>
{
    public async Task Consume(ConsumeContext<InventoryReservationFailedEvent> context)
    {
        var evt = context.Message;
        var saga = await db.SagaStates
            .FirstOrDefaultAsync(s => s.OrderId == evt.OrderId);

        if (saga is null) return;

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
    }
}

public class PaymentSucceededConsumer(
    OrderDbContext db,
    IPublishEndpoint publishEndpoint,
    OrderReadModelUpdater readModelUpdater,
    ILogger<PaymentSucceededConsumer> logger)
    : IConsumer<PaymentSucceededEvent>
{
    public async Task Consume(ConsumeContext<PaymentSucceededEvent> context)
    {
        var evt = context.Message;

        // Use a transaction to prevent race condition with timeout service
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            // Lock the row while reading — prevents concurrent updates
            var saga = await db.SagaStates.FirstOrDefaultAsync(s => s.OrderId == evt.OrderId);

            if (saga is null)
            {
                await transaction.RollbackAsync();
                return;
            }

            // Guard — check AFTER acquiring lock
            if (saga.Status == SagaStatus.Cancelled ||
                saga.Status == SagaStatus.Failed ||
                saga.Status == SagaStatus.Completed ||
                saga.Status == SagaStatus.ReleasingInventory) // ← add this
            {
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

                await transaction.RollbackAsync();
                return;
            }

            logger.LogInformation(
                "Saga {SagaId}: Payment succeeded → committing inventory",
                saga.Id);

            await publishEndpoint.Publish(new CommitInventoryCommand
            {
                OrderId = saga.OrderId
            }, context.CancellationToken);

            saga.Status = SagaStatus.Completing;
            saga.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            await readModelUpdater.UpdateStatusAsync(
                saga.OrderId,
                OrderStatus.Confirmed,
                paymentStatus: "Succeeded");

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


    // ── Payment failed → release inventory (compensate) ───────────────────────────
    public class PaymentFailedConsumer(
    OrderDbContext db,
    IPublishEndpoint publishEndpoint,
    OrderReadModelUpdater readModelUpdater,
    ILogger<PaymentFailedConsumer> logger)
    : IConsumer<PaymentFailedEvent>
{
    public async Task Consume(ConsumeContext<PaymentFailedEvent> context)
    {
        var evt = context.Message;
        var saga = await db.SagaStates
            .FirstOrDefaultAsync(s => s.OrderId == evt.OrderId);

        if (saga is null) return;

        logger.LogWarning(
            "Saga {SagaId}: Payment failed ({Reason}) → releasing inventory",
            saga.Id, evt.Reason);

        // Send compensating command to release the inventory reservation
        await publishEndpoint.Publish(new ReleaseInventoryCommand
        {
            OrderId = saga.OrderId
        });

        saga.Status = SagaStatus.ReleasingInventory;
        saga.FailureReason = evt.Reason;
        saga.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await readModelUpdater.UpdateStatusAsync(saga.OrderId, OrderStatus.Pending, failureReason: evt.Reason);

    }
}

// ── Inventory released → saga cancelled ──────────────────────────────────────
public class InventoryReleasedConsumer(
    OrderDbContext db,
    OrderReadModelUpdater readModelUpdater,
    ILogger<InventoryReleasedConsumer> logger)
    : IConsumer<InventoryReleasedEvent>
{
    public async Task Consume(ConsumeContext<InventoryReleasedEvent> context)
    {
        var evt = context.Message;
        var saga = await db.SagaStates
            .FirstOrDefaultAsync(s => s.OrderId == evt.OrderId);

        if (saga is null) return;

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

        // In InventoryReleasedConsumer after saga cancelled:
        var duration = DateTime.UtcNow - saga.CreatedAt;
        OrderMetrics.RecordSagaCancelled(duration);
    }
}