using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.PaymentService.Consumers;
using ShopEasy.PaymentService.Data;
using ShopEasy.Shared.Contracts;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Observability;

// Money the shop took, was refused and gave back, counted once however often a command is delivered.
[Collection(MetricsCollection.Name)]
public class PaymentMetricsTests : IDisposable
{
    private readonly PaymentDbContext _db = new(new DbContextOptionsBuilder<PaymentDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    public void Dispose() => _db.Dispose();

    private static ConsumeContext<T> Ctx<T>(T message) where T : class
    {
        var context = Substitute.For<ConsumeContext<T>>();
        context.Message.Returns(message);
        return context;
    }

    private async Task<(double Count, double Amount)> Read(string result) =>
        (await MetricValues.ReadAsync($"shopeasy_payments_total{{result=\"{result}\"}}"),
         await MetricValues.ReadAsync($"shopeasy_payments_amount_total{{result=\"{result}\"}}"));

    private async Task Pay(decimal amount, bool forceSuccess)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Testing:ForcePaymentSuccess"] = forceSuccess.ToString() }).Build();
        var command = new ProcessPaymentCommand { OrderId = Guid.NewGuid(), UserId = "u", UserEmail = "u@test.com", Currency = "USD", Amount = amount };
        var consumer = new ProcessPaymentConsumer(_db, NullLogger<ProcessPaymentConsumer>.Instance, config);

        await consumer.Consume(Ctx(command));
        await consumer.Consume(Ctx(command)); // delivered twice: the second is skipped
    }

    [Fact]
    public async Task APaymentTheProviderAccepts_IsCountedOnce_WithItsAmount()
    {
        var (count, amount) = await Read("succeeded");

        await Pay(49.90m, forceSuccess: true);

        var after = await Read("succeeded");
        after.Count.Should().Be(count + 1);
        after.Amount.Should().BeApproximately(amount + 49.90, 0.001);
    }

    [Fact]
    public async Task APaymentTheProviderDeclines_IsCountedOnce_WithItsAmount()
    {
        var (count, amount) = await Read("declined");

        await Pay(12000m, forceSuccess: false); // above the single-transaction limit

        var after = await Read("declined");
        after.Count.Should().Be(count + 1);
        after.Amount.Should().BeApproximately(amount + 12000, 0.001);
    }

    [Fact]
    public async Task APaymentGivenBack_IsCountedOnce_AndNothingIsCountedWhenThereWasNoPayment()
    {
        var orderId = Guid.NewGuid();
        _db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(), OrderId = orderId, UserId = "u", Amount = 30m, Status = PaymentStatus.Succeeded,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        var (count, amount) = await Read("refunded");
        var consumer = new RefundPaymentConsumer(_db, NullLogger<RefundPaymentConsumer>.Instance);

        await consumer.Consume(Ctx(new RefundPaymentCommand { OrderId = orderId, Reason = "cancelled" }));
        await consumer.Consume(Ctx(new RefundPaymentCommand { OrderId = orderId, Reason = "cancelled" }));
        await consumer.Consume(Ctx(new RefundPaymentCommand { OrderId = Guid.NewGuid(), Reason = "no such payment" }));

        var after = await Read("refunded");
        after.Count.Should().Be(count + 1);
        after.Amount.Should().BeApproximately(amount + 30, 0.001);
    }
}
