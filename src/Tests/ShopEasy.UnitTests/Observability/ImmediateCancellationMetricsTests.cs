using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.ReadModels;
using ShopEasy.OrderService.Sagas;
using ShopEasy.OrderService.Services;
using ShopEasy.UnitTests.Sagas;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Observability;

// Cancellations that end the order on the spot are counted when they happen: a customer or an
// administrator cancelling before any stock is held, and a timeout that gives up on the order.
[Collection(MetricsCollection.Name)]
public class ImmediateCancellationMetricsTests : IDisposable
{
    private readonly OrderDbContext _db = new(new DbContextOptionsBuilder<OrderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
    private readonly ISagaLocking _locking = new InMemorySagaLocking();
    private readonly IPublishEndpoint _publish = Substitute.For<IPublishEndpoint>();

    public void Dispose() => _db.Dispose();

    private async Task<Guid> Order(SagaStatus? saga, int retries = 0)
    {
        var id = Guid.NewGuid();
        var longAgo = DateTime.UtcNow.AddMinutes(-5);
        _db.Orders.Add(new Order { Id = id, UserId = "owner", UserEmail = "o@test.com", TotalAmount = 20m, CreatedAt = longAgo, UpdatedAt = longAgo });
        if (saga is not null)
            _db.SagaStates.Add(new OrderSagaState
            {
                Id = Guid.NewGuid(), OrderId = id, Status = saga.Value, UserId = "owner", UserEmail = "o@test.com",
                TotalAmount = 20m, ItemsJson = "[]", RetryCount = retries, CreatedAt = longAgo, UpdatedAt = longAgo
            });
        await _db.SaveChangesAsync();
        return id;
    }

    private Task<CancelResult> Cancel(Guid orderId, string requester, bool admin) =>
        new OrderCancellationService(_db, _locking, _publish, new OrderReadModelUpdater(_db, NullLogger<OrderReadModelUpdater>.Instance),
            NullLogger<OrderCancellationService>.Instance).CancelAsync(orderId, requester, admin);

    private static Task<double> Cancelled(string reason) =>
        MetricValues.ReadAsync($"shopeasy_order_outcomes_total{{outcome=\"cancelled\",reason=\"{reason}\"}}");

    [Theory]
    [InlineData(null, "owner", false, "customer")]
    [InlineData(SagaStatus.Started, "owner", false, "customer")]
    [InlineData(SagaStatus.Started, "an-admin", true, "admin")]
    public async Task ACancellationBeforeAnyStockIsHeld_IsCountedOnce(SagaStatus? saga, string requester, bool admin, string reason)
    {
        var orderId = await Order(saga);
        var before = await Cancelled(reason);

        await Cancel(orderId, requester, admin);
        await Cancel(orderId, requester, admin); // asking again changes nothing

        (await Cancelled(reason)).Should().Be(before + 1);
    }

    [Fact]
    public async Task ACancellationWhileStockIsReserved_WaitsForTheRelease_BeforeItIsCounted()
    {
        var orderId = await Order(SagaStatus.ReservingInventory);
        var before = await Cancelled("customer");

        await Cancel(orderId, "owner", admin: false);

        (await Cancelled("customer")).Should().Be(before);
    }

    [Theory]
    [InlineData(SagaStatus.Started, 0, "timeout")]
    [InlineData(SagaStatus.ReservingInventory, 0, "timeout")]
    [InlineData(SagaStatus.ReleasingInventory, 3, "release_not_confirmed")]
    public async Task AnOrderTheTimeoutGivesUpOn_IsCountedOnce(SagaStatus stuckIn, int retries, string reason)
    {
        await Order(stuckIn, retries);
        var before = await Cancelled(reason);
        var timeouts = new SagaTimeoutService(Substitute.For<IServiceScopeFactory>(), NullLogger<SagaTimeoutService>.Instance);

        await timeouts.CheckForTimedOutSagasPublic(_db, _publish, _locking, CancellationToken.None);
        await timeouts.CheckForTimedOutSagasPublic(_db, _publish, _locking, CancellationToken.None); // the saga is over now

        (await Cancelled(reason)).Should().Be(before + 1);
    }
}
