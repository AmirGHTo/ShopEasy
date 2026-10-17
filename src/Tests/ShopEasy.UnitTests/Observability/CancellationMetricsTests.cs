using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.ReadModels;
using ShopEasy.OrderService.Sagas;
using ShopEasy.Shared.Contracts;
using ShopEasy.UnitTests.Sagas;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Observability;

// A cancelled order is counted once, with why: no stock, a declined payment, the customer, an
// administrator, a timeout, or a commit that failed after the payment.
[Collection(MetricsCollection.Name)]
public class CancellationMetricsTests : IDisposable
{
    private readonly OrderDbContext _db = new(new DbContextOptionsBuilder<OrderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
    private readonly InMemorySagaLocking _locking = new();
    private OrderReadModelUpdater ReadModel => new(_db, NullLogger<OrderReadModelUpdater>.Instance);

    public void Dispose() => _db.Dispose();

    private async Task<Guid> Saga(SagaStatus status, string? failureReason = null)
    {
        var orderId = Guid.NewGuid();
        _db.SagaStates.Add(new OrderSagaState
        {
            Id = Guid.NewGuid(), OrderId = orderId, Status = status, UserId = "u", UserEmail = "u@test.com",
            TotalAmount = 50m, ItemsJson = "[]", FailureReason = failureReason, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return orderId;
    }

    private static ConsumeContext<T> Ctx<T>(T message) where T : class
    {
        var context = Substitute.For<ConsumeContext<T>>();
        context.Message.Returns(message);
        return context;
    }

    private static Task<double> Cancelled(string reason) =>
        MetricValues.ReadAsync($"shopeasy_order_outcomes_total{{outcome=\"cancelled\",reason=\"{reason}\"}}");

    [Theory]
    [InlineData("Payment declined by issuing bank", "payment_declined")]
    [InlineData("Cancelled by customer", "customer")]
    [InlineData("Cancelled by admin", "admin")]
    [InlineData("Timed out waiting for payment", "timeout")]
    public async Task WhenTheStockIsReleased_TheCancellationIsCountedByWhatStartedIt(string failureReason, string expected)
    {
        var orderId = await Saga(SagaStatus.ReleasingInventory, failureReason);
        var before = await Cancelled(expected);
        var consumer = new InventoryReleasedConsumer(_db, _locking, ReadModel, NullLogger<InventoryReleasedConsumer>.Instance);

        await consumer.Consume(Ctx(new InventoryReleasedEvent { OrderId = orderId }));
        await consumer.Consume(Ctx(new InventoryReleasedEvent { OrderId = orderId })); // a duplicate changes nothing

        (await Cancelled(expected)).Should().Be(before + 1);
    }

    [Fact]
    public async Task AnOrderInventoryCannotFill_IsCountedAsCancelledForStock()
    {
        var orderId = await Saga(SagaStatus.ReservingInventory);
        var before = await Cancelled("stock_unavailable");

        await new InventoryReservationFailedConsumer(_db, _locking, ReadModel, NullLogger<InventoryReservationFailedConsumer>.Instance)
            .Consume(Ctx(new InventoryReservationFailedEvent { OrderId = orderId, Reason = "Insufficient stock" }));

        (await Cancelled("stock_unavailable")).Should().Be(before + 1);
    }

    [Fact]
    public async Task ACommitThatFailsAfterThePayment_IsCountedOnce_AsCancelled()
    {
        var orderId = await Saga(SagaStatus.Completing);
        var before = await Cancelled("inventory_commit_failed");
        var consumer = new InventoryCommitFailedConsumer(
            _db, _locking, Substitute.For<IPublishEndpoint>(), ReadModel, NullLogger<InventoryCommitFailedConsumer>.Instance);

        await consumer.Consume(Ctx(new InventoryCommitFailedEvent { OrderId = orderId, Reason = "No reservation" }));
        await consumer.Consume(Ctx(new InventoryCommitFailedEvent { OrderId = orderId, Reason = "No reservation" }));

        (await Cancelled("inventory_commit_failed")).Should().Be(before + 1);
    }
}
