using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.InventoryService.Consumers;
using ShopEasy.InventoryService.Data;
using ShopEasy.Shared.Contracts;
using Xunit;

namespace ShopEasy.UnitTests.Inventory;

public class ReserveInventoryConsumerTests : IDisposable
{
    private static readonly Guid ProductA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid ProductB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private readonly InventoryTestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private ReserveInventoryConsumer CreateConsumer() =>
        new(_env.Db, _env.Locking, _env.Cache, NullLogger<ReserveInventoryConsumer>.Instance);

    private static ReserveInventoryCommand Command(Guid orderId, params (Guid Product, int Qty)[] lines) =>
        new()
        {
            OrderId = orderId,
            Items = lines.Select(l => new OrderLineItem
            {
                ProductId = l.Product,
                ProductName = "Product " + l.Product.ToString()[^1],
                Quantity = l.Qty,
                UnitPrice = 1m
            }).ToList()
        };

    private static async Task<ConsumeContext<ReserveInventoryCommand>> Run(
        ReserveInventoryConsumer consumer, ReserveInventoryCommand command)
    {
        var context = InventoryTestEnvironment.Context(command);
        await consumer.Consume(context);
        return context;
    }

    [Fact]
    public async Task WhenStockIsAvailable_ReservesItAndPublishesReserved()
    {
        var orderId = Guid.NewGuid();
        var item = _env.AddItem(ProductA, quantity: 10);

        var context = await Run(CreateConsumer(), Command(orderId, (ProductA, 3)));

        item.ReservedQty.Should().Be(3);
        item.Quantity.Should().Be(10);              // not deducted until commit
        item.AvailableQuantity.Should().Be(7);
        _env.LedgerCount(orderId, StockReasons.Reserved(orderId)).Should().Be(1);
        _env.Db.ProcessedEvents.Count(e => e.EventId == orderId).Should().Be(1);
        await context.Received(1).Publish(
            Arg.Is<InventoryReservedEvent>(e => e.OrderId == orderId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenStockIsInsufficient_PublishesFailureAndChangesNothing()
    {
        var orderId = Guid.NewGuid();
        var item = _env.AddItem(ProductA, quantity: 2);

        var context = await Run(CreateConsumer(), Command(orderId, (ProductA, 3)));

        item.ReservedQty.Should().Be(0);
        _env.Db.StockTransactions.Count().Should().Be(0);
        _env.Db.ProcessedEvents.Count().Should().Be(0);
        await context.Received(1).Publish(
            Arg.Is<InventoryReservationFailedEvent>(e =>
                e.OrderId == orderId && e.Reason.Contains("Insufficient stock")),
            Arg.Any<CancellationToken>());
        await context.DidNotReceive().Publish(
            Arg.Any<InventoryReservedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReservedStockIsNotAvailableToOtherOrders()
    {
        var item = _env.AddItem(ProductA, quantity: 10, reserved: 8);   // only 2 available

        var context = await Run(CreateConsumer(), Command(Guid.NewGuid(), (ProductA, 3)));

        item.ReservedQty.Should().Be(8);
        await context.Received(1).Publish(
            Arg.Any<InventoryReservationFailedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenOneProductIsShort_NothingIsReserved()
    {
        var orderId = Guid.NewGuid();
        var a = _env.AddItem(ProductA, quantity: 10);
        var b = _env.AddItem(ProductB, quantity: 1);

        var context = await Run(CreateConsumer(), Command(orderId, (ProductA, 2), (ProductB, 5)));

        a.ReservedQty.Should().Be(0);
        b.ReservedQty.Should().Be(0);
        _env.Db.StockTransactions.Count().Should().Be(0);
        await context.Received(1).Publish(
            Arg.Any<InventoryReservationFailedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SameProductOnSeveralLines_IsReservedOnceWithTheTotal()
    {
        var orderId = Guid.NewGuid();
        var item = _env.AddItem(ProductA, quantity: 10);

        await Run(CreateConsumer(), Command(orderId, (ProductA, 2), (ProductA, 3)));

        item.ReservedQty.Should().Be(5);
        _env.LedgerCount(orderId, StockReasons.Reserved(orderId)).Should().Be(1);
    }

    [Fact]
    public async Task DuplicateCommand_DoesNotReserveTwice_ButReConfirms()
    {
        var orderId = Guid.NewGuid();
        var item = _env.AddItem(ProductA, quantity: 10);
        var consumer = CreateConsumer();
        var command = Command(orderId, (ProductA, 3));

        await Run(consumer, command);
        var second = await Run(consumer, command);

        item.ReservedQty.Should().Be(3);
        _env.LedgerCount(orderId, StockReasons.Reserved(orderId)).Should().Be(1);
        await second.Received(1).Publish(
            Arg.Is<InventoryReservedEvent>(e => e.OrderId == orderId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownProduct_IsRejected()
    {
        var context = await Run(CreateConsumer(), Command(Guid.NewGuid(), (ProductA, 1)));

        await context.Received(1).Publish(
            Arg.Is<InventoryReservationFailedEvent>(e => e.Reason.Contains("Unknown product")),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public async Task NonPositiveQuantity_IsRejected(int quantity)
    {
        var item = _env.AddItem(ProductA, quantity: 10);

        var context = await Run(CreateConsumer(), Command(Guid.NewGuid(), (ProductA, quantity)));

        item.ReservedQty.Should().Be(0);
        await context.Received(1).Publish(
            Arg.Is<InventoryReservationFailedEvent>(e => e.Reason.Contains("Invalid quantity")),
            Arg.Any<CancellationToken>());
    }
}
