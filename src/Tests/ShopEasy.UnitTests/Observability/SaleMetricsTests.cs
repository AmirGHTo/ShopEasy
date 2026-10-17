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
using ShopEasy.UnitTests.Orders;
using ShopEasy.UnitTests.Sagas;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Observability;

// An order is "placed" when accepted; it is a sale, with its amount, once the stock is taken.
[Collection(MetricsCollection.Name)]
public class SaleMetricsTests
{
    private const string Placed = "shopeasy_orders_placed_total{status=\"accepted\"}";
    private const string Sales = "shopeasy_order_outcomes_total{outcome=\"confirmed\",reason=\"completed\"}";
    private const string Revenue = "shopeasy_orders_confirmed_amount_total";

    private static OrderDbContext NewDb() => new(new DbContextOptionsBuilder<OrderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    [Fact]
    public async Task AnAcceptedOrder_IsCountedAsPlaced()
    {
        using var db = NewDb();
        var before = await MetricValues.ReadAsync(Placed);

        var outcome = await new OrderPlacementHarness().PlaceAsync(db, OrderPlacementHarness.Order(OrderPlacementHarness.Desk, 1));

        outcome.StatusCode.Should().Be(201);
        (await MetricValues.ReadAsync(Placed)).Should().Be(before + 1);
    }

    [Fact]
    public async Task AConfirmedOrder_IsCountedOnceAsASale_WithItsAmount_EvenWhenTheEventIsDeliveredTwice()
    {
        using var db = NewDb();
        var orderId = Guid.NewGuid();
        db.SagaStates.Add(new OrderSagaState
        {
            Id = Guid.NewGuid(), OrderId = orderId, Status = SagaStatus.Completing, UserId = "u", UserEmail = "u@test.com",
            TotalAmount = 80m, ItemsJson = "[]", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var (sales, revenue) = (await MetricValues.ReadAsync(Sales), await MetricValues.ReadAsync(Revenue));
        var consumer = new InventoryCommittedConsumer(db, new InMemorySagaLocking(),
            new OrderReadModelUpdater(db, NullLogger<OrderReadModelUpdater>.Instance), NullLogger<InventoryCommittedConsumer>.Instance);
        var context = Substitute.For<ConsumeContext<InventoryCommittedEvent>>();
        context.Message.Returns(new InventoryCommittedEvent { OrderId = orderId });

        await consumer.Consume(context);
        await consumer.Consume(context);

        (await MetricValues.ReadAsync(Sales)).Should().Be(sales + 1);
        (await MetricValues.ReadAsync(Revenue)).Should().Be(revenue + 80);
    }
}
