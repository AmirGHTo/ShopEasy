using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using ShopEasy.OrderService.Controllers;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Idempotency;
using ShopEasy.OrderService.Services;
using Xunit;
using static ShopEasy.UnitTests.Orders.OrderPlacementHarness;

namespace ShopEasy.UnitTests.Orders;

// POST /api/orders with an Idempotency-Key: a client that repeats a request because it did not see
// the answer must get the original order back, not a second one. (The race between two copies in
// flight at the same moment needs a real database: OrderIdempotencyPostgresTests.)
public class OrderPlacementIdempotencyTests : IDisposable
{
    private readonly OrderDbContext _db = new(new DbContextOptionsBuilder<OrderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private readonly OrderPlacementHarness _orders = new();

    public void Dispose() => _db.Dispose();

    private Task<OrderPlacementHarness.Outcome> Place(
        PlaceOrderRequest request, string? key = null, string user = "user-1") =>
        _orders.PlaceAsync(_db, request, key, user);

    [Fact]
    public async Task WithoutAKey_TwoIdenticalRequests_PlaceTwoOrders()
    {
        var first = await Place(Order(Desk, 1));
        var second = await Place(Order(Desk, 1));

        first.StatusCode.Should().Be(201);
        second.StatusCode.Should().Be(201);
        _orders.OrderIdOf(second).Should().NotBe(_orders.OrderIdOf(first));
        (await _db.Orders.CountAsync()).Should().Be(2);
        (await _db.IdempotencyRecords.CountAsync()).Should().Be(0, "nothing is remembered without a key");
    }

    [Fact]
    public async Task ARepeatedRequest_ReturnsTheOriginalOrder_AndPlacesNoSecondOne()
    {
        var first = await Place(Order(Desk, 2, "gift"), key: "attempt-1");
        var retry = await Place(Order(Desk, 2, "gift"), key: "attempt-1");

        first.Result.Should().BeOfType<CreatedAtActionResult>();
        first.Replayed.Should().BeFalse();

        retry.StatusCode.Should().Be(201);
        retry.Replayed.Should().BeTrue("the client can tell the answer was replayed");
        retry.Response.Headers.Location.ToString().Should().Be($"/api/orders/{_orders.OrderIdOf(first)}");
        _orders.OrderIdOf(retry).Should().Be(_orders.OrderIdOf(first));
        _orders.BodyOf(retry).Should().Be(_orders.BodyOf(first), "the stored response is sent as it was");

        (await _db.Orders.CountAsync()).Should().Be(1);
        (await _db.OutboxEntries.CountAsync()).Should().Be(1, "the saga starts once");
        (await _db.OrderSummaries.CountAsync()).Should().Be(1);
        (await _db.IdempotencyRecords.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ARetryIsAnsweredFromTheStoredResponse_WithoutCallingTheCatalogOrTheStockAgain()
    {
        await Place(Order(Desk, 1), key: "k1");
        await _orders.Catalog.ReceivedWithAnyArgs(1).GetProductsAsync(default!, default);
        await _orders.Stock.ReceivedWithAnyArgs(1).GetStockBatchAsync(default!, default);

        await Place(Order(Desk, 1), key: "k1");

        await _orders.Catalog.ReceivedWithAnyArgs(1).GetProductsAsync(default!, default);
        await _orders.Stock.ReceivedWithAnyArgs(1).GetStockBatchAsync(default!, default);
    }

    [Fact]
    public async Task TheRetryStillSucceeds_EvenIfTheStockRanOutAndThePriceChangedInTheMeantime()
    {
        var first = await Place(Order(Desk, 5), key: "k1");

        _orders.Available = 0;
        _orders.Price = 999m;
        var retry = await Place(Order(Desk, 5), key: "k1");

        retry.StatusCode.Should().Be(201);
        _orders.OrderIdOf(retry).Should().Be(_orders.OrderIdOf(first));
        (await _db.Orders.SingleAsync()).TotalAmount.Should().Be(125m, "5 x the original price of 25");
    }

    [Fact]
    public async Task TheSameKeyForADifferentRequest_IsRejected_AndPlacesNothing()
    {
        await Place(Order(Desk, 1), key: "k1");

        var different = await Place(Order(Desk, 2), key: "k1");

        different.StatusCode.Should().Be(422);
        different.Result.Should().BeOfType<UnprocessableEntityObjectResult>();
        _orders.ErrorBodyOf(different).Should().Contain("different request");
        (await _db.Orders.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ADifferentNote_CountsAsADifferentRequest()
    {
        await Place(Order(Desk, 1, "for Anna"), key: "k1");

        (await Place(Order(Desk, 1, "for Ben"), key: "k1")).StatusCode.Should().Be(422);
    }

    [Fact]
    public async Task TheSameKeyFromAnotherUser_IsAnotherKey()
    {
        var alice = await Place(Order(Desk, 1), key: "shared", user: "alice");
        var bob = await Place(Order(Desk, 1), key: "shared", user: "bob");

        bob.StatusCode.Should().Be(201);
        bob.Replayed.Should().BeFalse();
        _orders.OrderIdOf(bob).Should().NotBe(_orders.OrderIdOf(alice));
        (await _db.Orders.CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("comma,separated")]
    public async Task AMalformedKey_IsRejectedBeforeAnythingElse(string key)
    {
        var outcome = await Place(Order(Desk, 1), key: key);

        outcome.StatusCode.Should().Be(400);
        (await _db.Orders.CountAsync()).Should().Be(0);
        await _orders.Catalog.DidNotReceiveWithAnyArgs().GetProductsAsync(default!, default);
    }

    [Fact]
    public async Task WhenTheKeyIsRequired_ARequestWithoutOneIsRejected_AndOneWithItIsAccepted()
    {
        _orders.Settings.RequireKey = true;

        var without = await Place(Order(Desk, 1));
        var with = await Place(Order(Desk, 1), key: "k1");

        without.StatusCode.Should().Be(400);
        without.Result.Should().BeOfType<BadRequestObjectResult>();
        _orders.ErrorBodyOf(without).Should().Contain("Idempotency-Key").And.Contain("required");
        with.StatusCode.Should().Be(201);
        (await _db.Orders.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ARequestThatCouldNotBeServed_DoesNotUseUpItsKey()
    {
        // the Product service is down: 503, no order, no key remembered
        _orders.Catalog.GetProductsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<Guid, CatalogProduct?>>(
                _ => throw new ProductCatalogUnavailableException("down"));
        var failed = await Place(Order(Desk, 1), key: "k1");
        failed.StatusCode.Should().Be(503);
        (await _db.IdempotencyRecords.CountAsync()).Should().Be(0);

        // it comes back; the client repeats the same request with the same key
        var recovered = new OrderPlacementHarness();
        var retry = await recovered.PlaceAsync(_db, Order(Desk, 1), key: "k1");

        retry.StatusCode.Should().Be(201);
        retry.Replayed.Should().BeFalse("this is the first request that actually placed an order");
        (await _db.Orders.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ARejectedRequest_DoesNotUseUpItsKeyEither()
    {
        _orders.Available = 0;
        var rejected = await Place(Order(Desk, 1), key: "k1");
        rejected.StatusCode.Should().Be(400);

        _orders.Available = 10; // restocked
        var retry = await Place(Order(Desk, 1), key: "k1");

        retry.StatusCode.Should().Be(201);
        retry.Replayed.Should().BeFalse();
    }

    [Fact]
    public async Task TheOrderItsOutboxEventItsReadModelRowAndTheKey_AreAllThere()
    {
        var outcome = await Place(Order(Desk, 3), key: "k1");
        var orderId = _orders.OrderIdOf(outcome);

        (await _db.Orders.SingleAsync()).Id.Should().Be(orderId);
        (await _db.OutboxEntries.SingleAsync()).EventType.Should().Be("OrderConfirmedEvent");
        (await _db.OrderSummaries.SingleAsync()).OrderId.Should().Be(orderId);
        var record = await _db.IdempotencyRecords.SingleAsync();
        record.OrderId.Should().Be(orderId);
        record.UserId.Should().Be("user-1");
        record.Key.Should().Be("k1");
        record.StatusCode.Should().Be(201);
        record.RequestHash.Should().Be(
            OrderRequestFingerprint.Compute([new RequestedLine(Desk, 3)], null));
    }
}
