using MassTransit;
using Microsoft.EntityFrameworkCore;
using ShopEasy.PaymentService.Data;
using ShopEasy.PaymentService.Metrics;
using ShopEasy.Shared.Contracts;

namespace ShopEasy.PaymentService.Consumers;

public class RefundPaymentConsumer(
    PaymentDbContext db,
    ILogger<RefundPaymentConsumer> logger) : IConsumer<RefundPaymentCommand>
{
    public async Task Consume(ConsumeContext<RefundPaymentCommand> context)
    {
        var cmd = context.Message;
        logger.LogWarning(
            "Issuing refund for Order {OrderId}. Reason: {Reason}",
            cmd.OrderId, cmd.Reason);

        var payment = await db.Payments
            .FirstOrDefaultAsync(p => p.OrderId == cmd.OrderId);

        if (payment is null)
        {
            logger.LogWarning(
                "No payment found for Order {OrderId} — nothing to refund",
                cmd.OrderId);
            return;
        }

        // Mark payment as refunded; a repeated command refunds nothing more and is not counted again
        var alreadyRefunded = payment.Status == PaymentStatus.Refunded;
        payment.Status = PaymentStatus.Refunded;
        payment.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        if (!alreadyRefunded)
            PaymentMetrics.RecordRefunded(payment.Amount);

        logger.LogWarning(
            "Payment {PaymentId} refunded for Order {OrderId}. Amount: {Amount}",
            payment.Id, cmd.OrderId, payment.Amount);
    }
}