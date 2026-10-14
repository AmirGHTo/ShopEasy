using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Services;
using ShopEasy.Shared.Contracts;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Messaging;

// The outbox delivery worker must not touch pending entries while the bus is not ready.
// (The delivery itself uses FOR UPDATE SKIP LOCKED, so it is covered against PostgreSQL in
// Infrastructure/OutboxDeliveryPostgresTests.cs.)
public class OutboxDeliveryGateTests
{
    private static (OutboxDeliveryService Service, IPublishEndpoint Publish, ServiceProvider Provider,
        ListLogger<OutboxDeliveryService> Log) Create(IBrokerReadiness readiness)
    {
        var publish = Substitute.For<IPublishEndpoint>();
        var databaseName = Guid.NewGuid().ToString();

        var services = new ServiceCollection();
        services.AddDbContext<OrderDbContext>(o => o
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped(_ => publish);
        var provider = services.BuildServiceProvider();

        var log = new ListLogger<OutboxDeliveryService>();
        var service = new OutboxDeliveryService(
            provider.GetRequiredService<IServiceScopeFactory>(), readiness, log);
        return (service, publish, provider, log);
    }

    private static async Task AddPendingEntryAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        db.OutboxEntries.Add(new OutboxEntry
        {
            Id = Guid.NewGuid(),
            EventType = nameof(OrderConfirmedEvent),
            Payload = "{}",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task WhenTheBusIsNotReady_NothingIsPublished_AndRetriesAreNotConsumed()
    {
        var readiness = Substitute.For<IBrokerReadiness>();
        readiness.IsReadyAsync(Arg.Any<CancellationToken>()).Returns(false);
        var (service, publish, provider, _) = Create(readiness);
        await AddPendingEntryAsync(provider);

        for (var cycle = 0; cycle < 3; cycle++)
            await service.DeliverPendingMessages(CancellationToken.None);

        await publish.DidNotReceiveWithAnyArgs().Publish(default(OrderConfirmedEvent)!, default);

        using var scope = provider.CreateScope();
        var entry = await scope.ServiceProvider.GetRequiredService<OrderDbContext>()
            .OutboxEntries.SingleAsync();
        entry.RetryCount.Should().Be(0);
        entry.DeliveredAt.Should().BeNull();
    }

    [Fact]
    public async Task WhenReadinessCannotBeDetermined_TreatsTheBusAsNotReady()
    {
        var readiness = Substitute.For<IBrokerReadiness>();
        readiness.IsReadyAsync(Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("health check blew up"));
        var (service, publish, provider, _) = Create(readiness);
        await AddPendingEntryAsync(provider);

        await service.DeliverPendingMessages(CancellationToken.None);

        await publish.DidNotReceiveWithAnyArgs().Publish(default(OrderConfirmedEvent)!, default);
        using var scope = provider.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<OrderDbContext>()
            .OutboxEntries.SingleAsync()).RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task ReadinessChangesAreLoggedOnce_NotOnEveryCycle()
    {
        var readiness = Substitute.For<IBrokerReadiness>();
        readiness.IsReadyAsync(Arg.Any<CancellationToken>()).Returns(false);
        var (service, _, provider, log) = Create(readiness);
        await AddPendingEntryAsync(provider);

        for (var cycle = 0; cycle < 5; cycle++)
            await service.DeliverPendingMessages(CancellationToken.None);

        log.Messages(Microsoft.Extensions.Logging.LogLevel.Warning)
            .Count(m => m.Contains("not ready")).Should().Be(1);
    }
}
