using MassTransit;
using Microsoft.EntityFrameworkCore;
using ShopEasy.PaymentService.Data;
using ShopEasy.Shared.Contracts;

namespace ShopEasy.PaymentService.Consumers;

public class ProcessPaymentConsumer(
    PaymentDbContext db,
    ILogger<ProcessPaymentConsumer> logger, IConfiguration config) : IConsumer<ProcessPaymentCommand>
{
    public async Task Consume(ConsumeContext<ProcessPaymentCommand> context)
    {
        var cmd = context.Message;
        logger.LogInformation(
            "Processing payment for Order {OrderId}, Amount: {Amount:C}",
            cmd.OrderId, cmd.Amount);

        // Idempotency check
        var alreadyProcessed = await db.ProcessedEvents
            .AnyAsync(e => e.EventId == cmd.OrderId);

        if (alreadyProcessed)
        {
            logger.LogWarning(
                "Duplicate ProcessPaymentCommand for Order {OrderId} — skipping",
                cmd.OrderId);

            var existing = await db.Payments
                .FirstOrDefaultAsync(p => p.OrderId == cmd.OrderId);

            // Don't republish if already refunded — order was cancelled
            if (existing?.Status == PaymentStatus.Refunded)
            {
                logger.LogWarning(
                    "Payment for Order {OrderId} was already refunded — ignoring duplicate",
                    cmd.OrderId);
                return;
            }

            if (existing?.Status == PaymentStatus.Succeeded)
                await context.Publish(new PaymentSucceededEvent
                {
                    OrderId = cmd.OrderId,
                    PaymentId = existing.Id,
                    Amount = existing.Amount
                });
            else
                await context.Publish(new PaymentFailedEvent
                {
                    OrderId = cmd.OrderId,
                    Reason = existing?.FailureReason ?? "Unknown"
                });
            return;
        }

        var (success, failureReason) = SimulatePaymentProcessing(cmd.Amount);

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = cmd.OrderId,
            UserId = cmd.UserId,
            Amount = cmd.Amount,
            Status = success ? PaymentStatus.Succeeded : PaymentStatus.Failed,
            FailureReason = failureReason,
            Provider = "simulated",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            db.Payments.Add(payment);
            db.ProcessedEvents.Add(new ProcessedEvent
            {
                EventId = cmd.OrderId,
                EventType = nameof(ProcessPaymentCommand),
                ProcessedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            logger.LogError(ex,
                "Failed to save payment for Order {OrderId}", cmd.OrderId);
            throw;
        }

        if (success)
        {
            logger.LogInformation(
                "Payment {PaymentId} succeeded for Order {OrderId}",
                payment.Id, cmd.OrderId);
            await context.Publish(new PaymentSucceededEvent
            {
                OrderId = cmd.OrderId,
                PaymentId = payment.Id,
                Amount = cmd.Amount
            });
        }
        else
        {
            logger.LogWarning(
                "Payment FAILED for Order {OrderId}: {Reason}",
                cmd.OrderId, failureReason);
            await context.Publish(new PaymentFailedEvent
            {
                OrderId = cmd.OrderId,
                Reason = failureReason!
            });
        }
    }

    private (bool success, string? failureReason) SimulatePaymentProcessing(decimal amount)
    {
        // Force success via config — for testing refund scenario only
        if (config.GetValue<bool>("Testing:ForcePaymentSuccess"))
            return (true, null);

        if (amount > 10000)
            return (false, "Amount exceeds single transaction limit");

        return new Random().Next(1, 11) <= 9
            ? (true, null)
            : (false, "Payment declined by issuing bank");
    }
}