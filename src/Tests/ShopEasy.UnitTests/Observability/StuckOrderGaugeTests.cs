using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Services;
using ShopEasy.UnitTests.Sagas;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Observability;

// How many orders are still in flight and how long the oldest has been: the age is what rises
// when an order is stuck, long before anyone writes in.
[Collection(MetricsCollection.Name)]
public class StuckOrderGaugeTests : IDisposable
{
    private readonly OrderDbContext _db = new(new DbContextOptionsBuilder<OrderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    public void Dispose() => _db.Dispose();

    private void Saga(SagaStatus status, int createdMinutesAgo) => _db.SagaStates.Add(new OrderSagaState
    {
        Id = Guid.NewGuid(), OrderId = Guid.NewGuid(), Status = status, UserId = "u", UserEmail = "u@test.com", TotalAmount = 20m,
        ItemsJson = "[]", CreatedAt = DateTime.UtcNow.AddMinutes(-createdMinutesAgo), UpdatedAt = DateTime.UtcNow
    });

    private async Task<(double Active, double OldestAge)> CycleAsync()
    {
        await _db.SaveChangesAsync();
        await new SagaTimeoutService(Substitute.For<IServiceScopeFactory>(), NullLogger<SagaTimeoutService>.Instance)
            .CheckForTimedOutSagasPublic(_db, Substitute.For<IPublishEndpoint>(), new InMemorySagaLocking(), CancellationToken.None);
        return (await MetricValues.ReadAsync("shopeasy_sagas_active"),
                await MetricValues.ReadAsync("shopeasy_saga_oldest_active_age_seconds"));
    }

    [Fact]
    public async Task OnlyOrdersStillInFlightAreCounted_AndTheOldestSetsTheAge()
    {
        Saga(SagaStatus.Started, createdMinutesAgo: 1);
        Saga(SagaStatus.ProcessingPayment, createdMinutesAgo: 10);
        Saga(SagaStatus.Completed, createdMinutesAgo: 60);   // finished: not in flight
        Saga(SagaStatus.Cancelled, createdMinutesAgo: 60);
        Saga(SagaStatus.Failed, createdMinutesAgo: 60);

        var (active, oldestAge) = await CycleAsync();

        active.Should().Be(2);
        oldestAge.Should().BeApproximately(600, 5);
    }

    [Fact]
    public async Task WhenNothingIsInFlight_BothGaugesFallBackToZero()
    {
        Saga(SagaStatus.Started, createdMinutesAgo: 5);
        await CycleAsync();                  // the gauges now hold a non-zero value
        _db.SagaStates.RemoveRange(_db.SagaStates);

        (await CycleAsync()).Should().Be((0, 0));
    }
}
