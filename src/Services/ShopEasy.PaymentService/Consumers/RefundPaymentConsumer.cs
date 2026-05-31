using MassTransit;
using Microsoft.EntityFrameworkCore;
using ShopEasy.PaymentService.Data;
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

        // Mark payment as refunded
        payment.Status = PaymentStatus.Refunded;
        payment.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        logger.LogWarning(
            "Payment {PaymentId} refunded for Order {OrderId}. Amount: {Amount}",
            payment.Id, cmd.OrderId, payment.Amount);
    }
}