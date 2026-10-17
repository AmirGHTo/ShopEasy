using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.InventoryService.Consumers;
using ShopEasy.InventoryService.Data;
using ShopEasy.Shared.Contracts;
using Xunit;

namespace ShopEasy.UnitTests.Inventory;

public class CommitInventoryConsumerTests : IDisposable
{
    private static readonly Guid ProductA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid ProductB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private readonly InventoryTestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private CommitInventoryConsumer CreateConsumer() =>
        new(_env.Db, _env.Locking, _env.Cache, NullLogger<CommitInventoryConsumer>.Instance);

    private static async Task<ConsumeContext<CommitInventoryCommand>> Run(
        CommitInventoryConsumer consumer, Guid orderId)
    {
        var context = InventoryTestEnvironment.Context(new CommitInventoryCommand { OrderId = orderId });
        await consumer.Consume(context);
        return context;
    }

    private void SeedReservation(Guid orderId, Guid productId, int qty) =>
        _env.AddLedger(orderId, productId, StockReasons.Reserved(orderId), -qty);

    [Fact]
    public async Task DeductsStockReleasesTheReservationAndPublishesCommitted()
    {
        var orderId = Guid.NewGuid();
        var item = _env.AddItem(ProductA, quantity: 10, reserved: 3);
        SeedReservation(orderId, ProductA, 3);

        var context = await Run(CreateConsumer(), orderId);

        item.Quantity.Should().Be(7);
        item.ReservedQty.Should().Be(0);
        _env.LedgerCount(orderId, StockReasons.Committed(orderId)).Should().Be(1);
        await context.Received(1).Publish(
            Arg.Is<InventoryCommittedEvent>(e => e.OrderId == orderId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CommitsEveryReservedProduct()
    {
        var orderId = Guid.NewGuid();
        var a = _env.AddItem(ProductA, quantity: 10, reserved: 2);
        var b = _env.AddItem(ProductB, quantity: 5, reserved: 1);
        SeedReservation(orderId, ProductA, 2);
        SeedReservation(orderId, ProductB, 1);

        await Run(CreateConsumer(), orderId);

        a.Quantity.Should().Be(8);
        b.Quantity.Should().Be(4);
        a.ReservedQty.Should().Be(0);
        b.ReservedQty.Should().Be(0);
    }

    [Fact]
    public async Task DuplicateCommand_DoesNotDeductTwice_ButReConfirms()
    {
        var orderId = Guid.NewGuid();
        var item = _env.AddItem(ProductA, quantity: 10, reserved: 3);
        SeedReservation(orderId, ProductA, 3);
        var consumer = CreateConsumer();

        await Run(consumer, orderId);
        var second = await Run(consumer, orderId);

        item.Quantity.Should().Be(7);
        item.ReservedQty.Should().Be(0);
        _env.LedgerCount(orderId, StockReasons.Committed(orderId)).Should().Be(1);
        await second.Received(1).Publish(
            Arg.Is<InventoryCommittedEvent>(e => e.OrderId == orderId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ACommitAfterTheStockWasPutBack_Fails_InsteadOfTakingTheStockAgain()
    {
        var orderId = Guid.NewGuid();
        var item = _env.AddItem(ProductA, quantity: 10, reserved: 0);
        SeedReservation(orderId, ProductA, 3);
        _env.AddLedger(orderId, ProductA, StockReasons.Committed(orderId), -3);
        _env.AddLedger(orderId, ProductA, StockReasons.Restocked(orderId), 3);

        var context = await Run(CreateConsumer(), orderId);

        item.Quantity.Should().Be(10);
        await context.Received(1).Publish(
            Arg.Is<InventoryCommitFailedEvent>(e => e.OrderId == orderId), Arg.Any<CancellationToken>());
        await context.DidNotReceive().Publish(Arg.Any<InventoryCommittedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OtherOrdersReservationsAreLeftAlone()
    {
        var orderId = Guid.NewGuid();
        var item = _env.AddItem(ProductA, quantity: 10, reserved: 5);   // 3 ours + 2 someone else's
        SeedReservation(orderId, ProductA, 3);

        await Run(CreateConsumer(), orderId);

        item.Quantity.Should().Be(7);
        item.ReservedQty.Should().Be(2);
    }

    [Fact]
    public async Task WithoutAReservation_PublishesCommitFailed()
    {
        var orderId = Guid.NewGuid();
        var item = _env.AddItem(ProductA, quantity: 10);

        var context = await Run(CreateConsumer(), orderId);

        item.Quantity.Should().Be(10);
        await context.Received(1).Publish(
            Arg.Is<InventoryCommitFailedEvent>(e =>
                e.OrderId == orderId && e.Reason.Contains("No reservation")),
            Arg.Any<CancellationToken>());
        await context.DidNotReceive().Publish(
            Arg.Any<InventoryCommittedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AfterTheReservationWasReleased_PublishesCommitFailedAndDeductsNothing()
    {
        var orderId = Guid.NewGuid();
        var item = _env.AddItem(ProductA, quantity: 10, reserved: 0);   // already freed by the release
        SeedReservation(orderId, ProductA, 3);
        _env.AddLedger(orderId, ProductA, StockReasons.Released(orderId), 0);

        var context = await Run(CreateConsumer(), orderId);

        item.Quantity.Should().Be(10);
        item.ReservedQty.Should().Be(0);
        _env.LedgerCount(orderId, StockReasons.Committed(orderId)).Should().Be(0);
        await context.Received(1).Publish(
            Arg.Is<InventoryCommitFailedEvent>(e =>
                e.OrderId == orderId && e.Reason.Contains("released")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NeverDrivesStockBelowZero()
    {
        var orderId = Guid.NewGuid();
        // an admin reduced the stock below what was reserved
        var item = _env.AddItem(ProductA, quantity: 1, reserved: 3);
        SeedReservation(orderId, ProductA, 3);

        await Run(CreateConsumer(), orderId);

        item.Quantity.Should().Be(0);
        item.ReservedQty.Should().Be(0);
    }
}
