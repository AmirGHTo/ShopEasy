using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
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

// The Order service's own outbox publishes an entry again when marking it delivered fails after the
// publish worked. The receiving side can only recognise that copy if it carries the same MessageId
// as the first. These tests publish through a real (in-memory) MassTransit bus and read the id from
// the message it sent. They need PostgreSQL because the batch is claimed with FOR UPDATE SKIP LOCKED.
public class OutboxDeliveryMessageIdTests
{
    [RequiresPostgresFact]
    public async Task AnEntry_IsPublishedUnderItsOwnId_AlsoWhenItIsPublishedAgain()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("outbox_msgid");
        var readiness = Substitute.For<IBrokerReadiness>();
        readiness.IsReadyAsync(Arg.Any<CancellationToken>()).Returns(true);

        var services = new ServiceCollection();
        services.AddDbContext<OrderDbContext>(o => o.UseNpgsql(database.ConnectionString));
        services.AddMassTransitTestHarness();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var _ = provider.StopWithinLimit();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.StartWithinLimitAsync();

        var entryId = Guid.NewGuid();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            await db.Database.MigrateAsync();
            db.OutboxEntries.Add(new OutboxEntry
            {
                Id = entryId,
                EventType = nameof(OrderConfirmedEvent),
                Payload = System.Text.Json.JsonSerializer.Serialize(new OrderConfirmedEvent
                {
                    OrderId = Guid.NewGuid(), UserId = "u1", UserEmail = "u1@example.com",
                    TotalAmount = 10m, ConfirmedAt = DateTime.UtcNow, Items = []
                }),
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var service = new OutboxDeliveryService(
            provider.GetRequiredService<IServiceScopeFactory>(), readiness, new ListLogger<OutboxDeliveryService>());
        await service.DeliverPendingMessages(CancellationToken.None);

        // the state a failed "mark as delivered" leaves behind: published, but still pending
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            (await db.OutboxEntries.SingleAsync()).DeliveredAt = null;
            await db.SaveChangesAsync();
        }
        await service.DeliverPendingMessages(CancellationToken.None);

        // The harness lists a message once per MessageId, so one message after two publishes means the
        // second carried the id of the first. (With a fresh id each time there would be two.)
        var published = harness.Published.Select<OrderConfirmedEvent>().ToList();
        published.Should().ContainSingle().Which.Context.MessageId.Should().Be(entryId);
    }
}
