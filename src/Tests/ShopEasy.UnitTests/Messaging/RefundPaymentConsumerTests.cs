using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.PaymentService.Consumers;
using ShopEasy.PaymentService.Data;
using ShopEasy.Shared.Contracts;
using Xunit;

namespace ShopEasy.UnitTests.Messaging;

// A refund tells the saga when it is done, so the saga can go on to the next step of a cancellation.
public class RefundPaymentConsumerTests : IDisposable
{
    private readonly PaymentDbContext _db = new(new DbContextOptionsBuilder<PaymentDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    public void Dispose() => _db.Dispose();

    private async Task<ConsumeContext<RefundPaymentCommand>> Refund(Guid orderId)
    {
        var context = Substitute.For<ConsumeContext<RefundPaymentCommand>>();
        context.Message.Returns(new RefundPaymentCommand { OrderId = orderId, Reason = "cancelled" });
        await new RefundPaymentConsumer(_db, NullLogger<RefundPaymentConsumer>.Instance).Consume(context);
        return context;
    }

    private async Task<Guid> PaidOrder(decimal amount)
    {
        var orderId = Guid.NewGuid();
        _db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(), OrderId = orderId, UserId = "u", Amount = amount, Status = PaymentStatus.Succeeded,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return orderId;
    }

    [Fact]
    public async Task ARefundedPayment_IsReportedToTheSaga_WithItsAmount()
    {
        var orderId = await PaidOrder(42.50m);

        var context = await Refund(orderId);

        var payment = await _db.Payments.SingleAsync(p => p.OrderId == orderId);
        payment.Status.Should().Be(PaymentStatus.Refunded);
        await context.Received(1).Publish(
            Arg.Is<PaymentRefundedEvent>(e => e.OrderId == orderId && e.PaymentId == payment.Id && e.Amount == 42.50m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARepeatedRefundCommand_ReportsAgain_SoALostEventCanBeAskedForTwice()
    {
        var orderId = await PaidOrder(10m);
        await Refund(orderId);

        var again = await Refund(orderId);

        await again.Received(1).Publish(
            Arg.Is<PaymentRefundedEvent>(e => e.OrderId == orderId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnOrderWithoutAPayment_ReportsNothing()
    {
        var context = await Refund(Guid.NewGuid());

        await context.DidNotReceive().Publish(Arg.Any<PaymentRefundedEvent>(), Arg.Any<CancellationToken>());
    }
}
