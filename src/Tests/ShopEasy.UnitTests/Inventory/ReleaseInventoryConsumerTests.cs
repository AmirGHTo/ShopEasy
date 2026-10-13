using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.InventoryService.Consumers;
using ShopEasy.InventoryService.Data;
using ShopEasy.Shared.Contracts;
using Xunit;

namespace ShopEasy.UnitTests.Inventory;

public class ReleaseInventoryConsumerTests : IDisposable
{
    private static readonly Guid ProductA = Guid.Parse("00000000-0000-0000-0000-00000000000a");

    private readonly InventoryTestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private ReleaseInventoryConsumer CreateConsumer() =>
        new(_env.Db, _env.Locking, _env.Cache, NullLogger<ReleaseInventoryConsumer>.Instance);

    private static async Task<ConsumeContext<ReleaseInventoryCommand>> Run(
        ReleaseInventoryConsumer consumer, Guid orderId)
    {
        var context = InventoryTestEnvironment.Context(new ReleaseInventoryCommand { OrderId = orderId });
        await consumer.Consume(context);
        return context;
    }

    private void SeedReservation(Guid orderId, Guid productId, int qty) =>
        _env.AddLedger(orderId, productId, StockReasons.Reserved(orderId), -qty);

    [Fact]
    public async Task FreesTheReservationAndPublishesReleased()
    {
        var orderId = Guid.NewGuid();
        var item = _env.AddItem(ProductA, quantity: 10, reserved: 3);
        SeedReservation(orderId, ProductA, 3);

        var context = await Run(CreateConsumer(), orderId);

        item.ReservedQty.Should().Be(0);
        item.Quantity.Should().Be(10);
        _env.LedgerCount(orderId, StockReasons.Released(orderId)).Should().Be(1);
        await context.Received(1).Publish(
            Arg.Is<InventoryReleasedEvent>(e => e.OrderId == orderId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DuplicateCommand_DoesNotFreeStockTwice_ButReConfirms()
    {
        var orderId = Guid.NewGuid();
        var item = _env.AddItem(ProductA, quantity: 10, reserved: 5);   // 3 ours + 2 someone else's
        SeedReservation(orderId, ProductA, 3);
        var consumer = CreateConsumer();

        await Run(consumer, orderId);
        var second = await Run(consumer, orderId);

        item.ReservedQty.Should().Be(2);       // only OUR 3 were freed, and only once
        _env.LedgerCount(orderId, StockReasons.Released(orderId)).Should().Be(1);
        await second.Received(1).Publish(
            Arg.Is<InventoryReleasedEvent>(e => e.OrderId == orderId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AfterTheReservationWasCommitted_ChangesNothingAndPublishesNothing()
    {
        var orderId = Guid.NewGuid();
        var item = _env.AddItem(ProductA, quantity: 7, reserved: 0);    // state after a commit of 3
        SeedReservation(orderId, ProductA, 3);
        _env.AddLedger(orderId, ProductA, StockReasons.Committed(orderId), -3);

        var context = await Run(CreateConsumer(), orderId);

        item.Quantity.Should().Be(7);
        item.ReservedQty.Should().Be(0);
        _env.LedgerCount(orderId, StockReasons.Released(orderId)).Should().Be(0);
        await context.DidNotReceive().Publish(
            Arg.Any<InventoryReleasedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithoutAReservation_StillConfirmsSoTheSagaCanFinish()
    {
        var orderId = Guid.NewGuid();
        var item = _env.AddItem(ProductA, quantity: 10, reserved: 2);

        var context = await Run(CreateConsumer(), orderId);

        item.ReservedQty.Should().Be(2);
        await context.Received(1).Publish(
            Arg.Is<InventoryReleasedEvent>(e => e.OrderId == orderId), Arg.Any<CancellationToken>());
    }
}
