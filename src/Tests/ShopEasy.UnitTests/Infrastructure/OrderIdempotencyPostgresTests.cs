using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using ShopEasy.OrderService.Controllers;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Idempotency;
using ShopEasy.OrderService.Services;
using ShopEasy.UnitTests.Orders;
using Xunit;
using static ShopEasy.UnitTests.Orders.OrderPlacementHarness;

namespace ShopEasy.UnitTests.Infrastructure;

// Idempotent order placement against a real PostgreSQL, where the unique index on (UserId, Key)
// is what decides between requests with the same key that run at the same moment. See InfrastructureFacts.cs.
public class OrderIdempotencyPostgresTests
{
    private static DbContextOptions<OrderDbContext> OptionsFor(PostgresTestDatabase database) =>
        new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(database.ConnectionString).Options;

    private static async Task<DbContextOptions<OrderDbContext>> CreateOrderDatabaseAsync(PostgresTestDatabase database)
    {
        var options = OptionsFor(database);
        await using var db = new OrderDbContext(options);
        await db.Database.MigrateAsync(); // also proves the migration applies to a real database
        return options;
    }

    // Holds every request at the product lookup until all of them have got that far, so that all of
    // them have already seen "this key is new" before the first one saves anything
    private static void MakeRequestsOverlap(OrderPlacementHarness orders, int requests)
    {
        var arrived = 0;
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        orders.Catalog.GetProductsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (Interlocked.Increment(ref arrived) == requests) allArrived.SetResult();
                await allArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
                return (IReadOnlyDictionary<Guid, CatalogProduct?>)call.Arg<IEnumerable<Guid>>().Distinct()
                    .ToDictionary(id => id, id => (CatalogProduct?)new CatalogProduct(id, "Standing Desk", 25m, true));
            });
    }

    private static async Task<OrderPlacementHarness.Outcome[]> InParallel(
        OrderPlacementHarness orders, DbContextOptions<OrderDbContext> options,
        IEnumerable<(PlaceOrderRequest Request, string? Key)> requests) =>
        await Task.WhenAll(requests.Select(r => Task.Run(async () =>
        {
            await using var db = new OrderDbContext(options); // each request has its own scope
            return await orders.PlaceAsync(db, r.Request, r.Key);
        })));

    [RequiresPostgresFact]
    public async Task EightCopiesOfOneRequestInFlightAtOnce_PlaceExactlyOneOrder()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("idemrace");
        var options = await CreateOrderDatabaseAsync(database);
        var orders = new OrderPlacementHarness();
        MakeRequestsOverlap(orders, 8);

        var outcomes = await InParallel(orders, options,
            Enumerable.Range(0, 8).Select(_ => (Order(Desk, 2, "gift"), (string?)"one-key")));

        // everybody is told about the same order; one created it, the others replayed it
        outcomes.Should().OnlyContain(o => o.StatusCode == 201);
        outcomes.Select(orders.OrderIdOf).Distinct().Should().ContainSingle();
        outcomes.Count(o => o.Replayed).Should().Be(7);
        outcomes.Select(orders.BodyOf).Distinct().Should().ContainSingle("they all got the same response");

        // and nothing of the seven that lost is left behind: no second order, outbox event,
        // read model row, order line or key
        await using var check = new OrderDbContext(options);
        (await check.Orders.CountAsync()).Should().Be(1);
        (await check.OrderItems.CountAsync()).Should().Be(1);
        (await check.OutboxEntries.CountAsync()).Should().Be(1, "the saga must start once");
        (await check.OrderSummaries.CountAsync()).Should().Be(1);
        (await check.IdempotencyRecords.CountAsync()).Should().Be(1);
    }

    [RequiresPostgresFact]
    public async Task ConcurrentRequestsWithDifferentKeys_EachPlaceTheirOwnOrder()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("idemkeys");
        var options = await CreateOrderDatabaseAsync(database);
        var orders = new OrderPlacementHarness();
        MakeRequestsOverlap(orders, 4);

        var outcomes = await InParallel(orders, options,
            Enumerable.Range(0, 4).Select(i => (Order(Desk, 1), (string?)$"key-{i}")));

        outcomes.Should().OnlyContain(o => o.StatusCode == 201 && !o.Replayed);
        outcomes.Select(orders.OrderIdOf).Distinct().Should().HaveCount(4);
        await using var check = new OrderDbContext(options);
        (await check.Orders.CountAsync()).Should().Be(4);
    }

    [RequiresPostgresFact]
    public async Task ConcurrentRequestsWithoutAKey_AreNotDeduplicated()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("idemnone");
        var options = await CreateOrderDatabaseAsync(database);
        var orders = new OrderPlacementHarness();
        MakeRequestsOverlap(orders, 3);

        var outcomes = await InParallel(orders, options,
            Enumerable.Range(0, 3).Select(_ => (Order(Desk, 1), (string?)null)));

        outcomes.Select(orders.OrderIdOf).Distinct().Should().HaveCount(3, "without a key the server cannot know they are the same");
    }

    [RequiresPostgresFact]
    public async Task ConcurrentRequestsThatReuseOneKeyForDifferentOrders_OnlyOneWins_TheOthersAreRejected()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("idemclash");
        var options = await CreateOrderDatabaseAsync(database);
        var orders = new OrderPlacementHarness();
        MakeRequestsOverlap(orders, 4);

        var outcomes = await InParallel(orders, options,
            Enumerable.Range(1, 4).Select(quantity => (Order(Desk, quantity), (string?)"clash")));

        outcomes.Count(o => o.StatusCode == 201).Should().Be(1);
        outcomes.Count(o => o.StatusCode == 422).Should().Be(3);
        await using var check = new OrderDbContext(options);
        (await check.Orders.CountAsync()).Should().Be(1);
        (await check.IdempotencyRecords.CountAsync()).Should().Be(1);
    }

    [RequiresPostgresFact]
    public async Task ARetryAfterTheOriginalFinished_ReplaysItFromTheDatabase()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("idemretry");
        var options = await CreateOrderDatabaseAsync(database);
        var orders = new OrderPlacementHarness();

        await using var first = new OrderDbContext(options);
        var original = await orders.PlaceAsync(first, Order(Webcam, 1), "k1");
        await using var second = new OrderDbContext(options); // a later request, possibly on another instance
        var retry = await orders.PlaceAsync(second, Order(Webcam, 1), "k1");

        retry.Replayed.Should().BeTrue();
        orders.BodyOf(retry).Should().Be(orders.BodyOf(original));
    }

    // ── retention ────────────────────────────────────────────────────────────

    [RequiresPostgresFact]
    public async Task OnlyKeysOlderThanTheRetentionAreRemoved()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("idempurge");
        var options = await CreateOrderDatabaseAsync(database);
        var now = DateTime.UtcNow;

        await using (var db = new OrderDbContext(options))
        {
            IdempotencyRecord Record(string key, TimeSpan age) => new()
            {
                Id = Guid.NewGuid(), UserId = "u", Key = key, RequestHash = "h", OrderId = Guid.NewGuid(),
                StatusCode = 201, ResponseBody = "{}", CreatedAt = now - age
            };
            db.IdempotencyRecords.AddRange(
                Record("old", TimeSpan.FromHours(30)),
                Record("just-expired", TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1)),
                Record("recent", TimeSpan.FromHours(23)),
                Record("new", TimeSpan.Zero));
            await db.SaveChangesAsync();
        }

        await using (var db = new OrderDbContext(options))
        {
            var removed = await IdempotencyCleanupService.PurgeAsync(db, TimeSpan.FromHours(24), now);
            removed.Should().Be(2);
        }

        await using var check = new OrderDbContext(options);
        (await check.IdempotencyRecords.Select(r => r.Key).OrderBy(k => k).ToListAsync())
            .Should().Equal("new", "recent");
    }

    [RequiresPostgresFact]
    public async Task AKeyThatWasForgotten_IsAcceptedAsANewRequest()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("idemforget");
        var options = await CreateOrderDatabaseAsync(database);
        var orders = new OrderPlacementHarness();

        await using (var db = new OrderDbContext(options))
            await orders.PlaceAsync(db, Order(Desk, 1), "k1");
        await using (var db = new OrderDbContext(options))
            await IdempotencyCleanupService.PurgeAsync(db, TimeSpan.Zero, DateTime.UtcNow.AddMinutes(1));

        await using var later = new OrderDbContext(options);
        var again = await orders.PlaceAsync(later, Order(Desk, 1), "k1");

        again.Replayed.Should().BeFalse();
        (await later.Orders.CountAsync()).Should().Be(2);
    }
}
