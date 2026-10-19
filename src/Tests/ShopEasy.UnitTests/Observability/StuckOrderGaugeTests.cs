using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Sagas;
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
    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly OrderDbContext _db;

    public StuckOrderGaugeTests() => _db = new(new DbContextOptionsBuilder<OrderDbContext>()
        .UseInMemoryDatabase(_databaseName)
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
    public async Task TheRunningService_PublishesTheGaugesToo()
    {
        Saga(SagaStatus.Started, createdMinutesAgo: 1);
        Saga(SagaStatus.ProcessingPayment, createdMinutesAgo: 2);
        Saga(SagaStatus.Completing, createdMinutesAgo: 7);
        await _db.SaveChangesAsync();

        // the pass the service makes on its timer: it gets its database through a scope, like in production
        var services = new ServiceCollection();
        services.AddDbContext<OrderDbContext>(o => o
            .UseInMemoryDatabase(_databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped(_ => Substitute.For<IPublishEndpoint>());
        services.AddScoped<ISagaLocking, InMemorySagaLocking>();
        await using var provider = services.BuildServiceProvider();
        await new SagaTimeoutService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<SagaTimeoutService>.Instance)
            .CheckForTimedOutSagasPublic(CancellationToken.None);

        (await MetricValues.ReadAsync("shopeasy_sagas_active")).Should().Be(3);
        (await MetricValues.ReadAsync("shopeasy_saga_oldest_active_age_seconds")).Should().BeApproximately(420, 5);
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
