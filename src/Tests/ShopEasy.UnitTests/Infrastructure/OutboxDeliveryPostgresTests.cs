using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Services;
using ShopEasy.Shared.Contracts;
using ShopEasy.Shared.Messaging;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Infrastructure;

// The Order service's own outbox (OutboxEntries) delivered by OutboxDeliveryService, against a
// real PostgreSQL because the batch is claimed with FOR UPDATE SKIP LOCKED.
public class OutboxDeliveryPostgresTests
{
    private static async Task<(OutboxDeliveryService Service, IPublishEndpoint Publish, ServiceProvider Provider)>
        CreateAsync(PostgresTestDatabase database, bool busReady, int pendingEntries)
    {
        var publish = Substitute.For<IBus>();
        var readiness = Substitute.For<IBrokerReadiness>();
        readiness.IsReadyAsync(Arg.Any<CancellationToken>()).Returns(busReady);

        var services = new ServiceCollection();
        services.AddDbContext<OrderDbContext>(o => o.UseNpgsql(database.ConnectionString));
        services.AddScoped(_ => publish);
        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            await db.Database.MigrateAsync();
            for (var i = 0; i < pendingEntries; i++)
            {
                var evt = new OrderConfirmedEvent
                {
                    OrderId = Guid.NewGuid(), UserId = "u1", UserEmail = "u1@example.com",
                    TotalAmount = 10m, ConfirmedAt = DateTime.UtcNow, Items = []
                };
                db.OutboxEntries.Add(new OutboxEntry
                {
                    Id = Guid.NewGuid(),
                    EventType = nameof(OrderConfirmedEvent),
                    Payload = System.Text.Json.JsonSerializer.Serialize(evt),
                    CreatedAt = DateTime.UtcNow.AddMilliseconds(i)
                });
            }
            await db.SaveChangesAsync();
        }

        var service = new OutboxDeliveryService(
            provider.GetRequiredService<IServiceScopeFactory>(), readiness,
            new ListLogger<OutboxDeliveryService>());
        return (service, publish, provider);
    }

    private static async Task<List<OutboxEntry>> EntriesAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrderDbContext>()
            .OutboxEntries.AsNoTracking().OrderBy(e => e.CreatedAt).ToListAsync();
    }

    [RequiresPostgresFact]
    public async Task WhenTheBusIsReady_PendingEntriesArePublishedAndMarkedDelivered()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("outbox_ready");
        var (service, publish, provider) = await CreateAsync(database, busReady: true, pendingEntries: 3);
        await using var _ = provider;

        await service.DeliverPendingMessages(CancellationToken.None);

        await publish.Received(3).Publish(Arg.Any<OrderConfirmedEvent>(), Arg.Any<CancellationToken>());
        (await EntriesAsync(provider)).Should().OnlyContain(e => e.DeliveredAt != null && e.RetryCount == 0);
    }

    [RequiresPostgresFact]
    public async Task WhenTheBusIsNotReady_NothingIsPublished_AndNoRetryIsSpent()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("outbox_down");
        var (service, publish, provider) = await CreateAsync(database, busReady: false, pendingEntries: 2);
        await using var _ = provider;

        await service.DeliverPendingMessages(CancellationToken.None);
        await service.DeliverPendingMessages(CancellationToken.None);

        await publish.DidNotReceiveWithAnyArgs().Publish(default(OrderConfirmedEvent)!, default);
        (await EntriesAsync(provider)).Should().OnlyContain(e => e.DeliveredAt == null && e.RetryCount == 0);
    }

    [RequiresPostgresFact]
    public async Task WhenPublishingFails_TheEntryIsRetriedLater_NotLost()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("outbox_fail");
        var (service, publish, provider) = await CreateAsync(database, busReady: true, pendingEntries: 1);
        await using var _ = provider;
        publish.Publish(Arg.Any<OrderConfirmedEvent>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("broker said no"));

        await service.DeliverPendingMessages(CancellationToken.None);

        var afterFailure = (await EntriesAsync(provider)).Single();
        afterFailure.DeliveredAt.Should().BeNull();
        afterFailure.RetryCount.Should().Be(1);

        // the next cycle succeeds
        publish.Publish(Arg.Any<OrderConfirmedEvent>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await service.DeliverPendingMessages(CancellationToken.None);

        (await EntriesAsync(provider)).Single().DeliveredAt.Should().NotBeNull();
    }

    [RequiresPostgresFact]
    public async Task APoisonEntry_IsDeadLetteredAfterItsRetries_AndDoesNotBlockTheOthers()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("outbox_poison");
        var (service, publish, provider) = await CreateAsync(database, busReady: true, pendingEntries: 1);
        await using var _ = provider;

        // an entry nobody can publish (unknown type), older than the good one
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            db.OutboxEntries.Add(new OutboxEntry
            {
                Id = Guid.NewGuid(), EventType = "SomethingRemoved", Payload = "{}",
                CreatedAt = DateTime.UtcNow.AddMinutes(-5)
            });
            await db.SaveChangesAsync();
        }

        // 5 attempts use up the poison entry's retries; the good entry waits behind it
        for (var cycle = 0; cycle < 5; cycle++)
            await service.DeliverPendingMessages(CancellationToken.None);

        var entries = await EntriesAsync(provider);
        var poison = entries.Single(e => e.EventType == "SomethingRemoved");
        poison.RetryCount.Should().Be(5);
        poison.DeliveredAt.Should().BeNull();
        await publish.DidNotReceiveWithAnyArgs().Publish(default(OrderConfirmedEvent)!, default);

        // the next cycle skips the dead-lettered entry and delivers the good one
        await service.DeliverPendingMessages(CancellationToken.None);

        (await EntriesAsync(provider)).Single(e => e.EventType == nameof(OrderConfirmedEvent))
            .DeliveredAt.Should().NotBeNull();
    }

    [RequiresPostgresFact]
    public async Task WhenALaterEntryFails_EarlierDeliveriesInTheBatchStayDelivered()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("outbox_partial");
        var (service, publish, provider) = await CreateAsync(database, busReady: true, pendingEntries: 3);
        await using var _ = provider;

        var calls = 0;
        publish.Publish(Arg.Any<OrderConfirmedEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 2
                ? Task.FromException(new InvalidOperationException("broker said no"))
                : Task.CompletedTask);

        await service.DeliverPendingMessages(CancellationToken.None);

        var entries = await EntriesAsync(provider);
        entries[0].DeliveredAt.Should().NotBeNull("it was published before the failure");
        entries[1].DeliveredAt.Should().BeNull();
        entries[1].RetryCount.Should().Be(1);
        entries[2].RetryCount.Should().Be(0, "the batch stops at the failure; the rest wait for the next cycle");
        entries[2].DeliveredAt.Should().BeNull();
    }

    [RequiresPostgresFact]
    public async Task TheCorrelationIdOfTheRequestThatPlacedTheOrder_IsRestoredWhilePublishing()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("outbox_corr");
        var (service, publish, provider) = await CreateAsync(database, busReady: true, pendingEntries: 2);
        await using var _ = provider;

        // the worker has no HTTP request: the ids come from the entries
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            var entries = await db.OutboxEntries.OrderBy(e => e.CreatedAt).ToListAsync();
            entries[0].CorrelationId = "req-1";
            // entries[1] stays without one (placed before correlation ids existed)
            await db.SaveChangesAsync();
        }

        var seen = new List<string?>();
        publish.Publish(Arg.Any<OrderConfirmedEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => { seen.Add(CorrelationContext.Current); return Task.CompletedTask; });

        await service.DeliverPendingMessages(CancellationToken.None);

        seen.Should().Equal("req-1", null);
        CorrelationContext.Current.Should().BeNull("the scope ends with each entry");
    }
}
