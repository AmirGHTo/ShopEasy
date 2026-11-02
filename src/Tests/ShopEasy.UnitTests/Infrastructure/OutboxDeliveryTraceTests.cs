using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Services;
using ShopEasy.OrderService.Tracing;
using ShopEasy.Shared.Contracts;
using ShopEasy.Shared.Messaging;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Infrastructure;

// The Order service's own outbox holds the event that starts an order's saga until a background loop
// delivers it. The loop has no request, so it starts its span inside the trace the entry remembers
// (OrderSpans): the request that placed the order, the delivery and the consumer that creates the
// saga are then one trace. Needs PostgreSQL (the batch is claimed with FOR UPDATE SKIP LOCKED).
public class OutboxDeliveryTraceTests
{
    private static readonly ActivitySource Test = new("ShopEasy.Tests.OutboxTrace");

    private sealed class SagaStarterProbe(ConcurrentQueue<(string TraceId, string ParentSpanId)> seen) : IConsumer<OrderConfirmedEvent>
    {
        public Task Consume(ConsumeContext<OrderConfirmedEvent> context)
        {
            seen.Enqueue((Activity.Current!.TraceId.ToHexString(), Activity.Current.ParentSpanId.ToHexString()));
            return Task.CompletedTask;
        }
    }

    [RequiresPostgresFact]
    public async Task TheDeliveredEvent_AndItsConsumer_AreInTheTraceOfTheRequestThatPlacedTheOrder()
    {
        using var spans = new SpanListener(OrderSpans.SourceName, Test.Name, "MassTransit");
        await using var database = await PostgresTestDatabase.CreateAsync("outbox_trace");
        var readiness = Substitute.For<IBrokerReadiness>();
        readiness.IsReadyAsync(Arg.Any<CancellationToken>()).Returns(true);
        var seen = new ConcurrentQueue<(string TraceId, string ParentSpanId)>();

        var services = new ServiceCollection();
        services.AddDbContext<OrderDbContext>(o => o.UseNpgsql(database.ConnectionString));
        services.AddSingleton(seen);
        services.AddMassTransitTestHarness(x => x.AddConsumer<SagaStarterProbe>());
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var _ = provider.StopWithinLimit();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.StartWithinLimitAsync();

        // the request that placed the order wrote the entry, with the context of its span
        var orderId = Guid.NewGuid();
        Activity request;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            await db.Database.MigrateAsync();
            using (Test.StartActivity("POST api/orders"))
            {
                request = Activity.Current!;
                db.OutboxEntries.Add(new OutboxEntry
                {
                    Id = Guid.NewGuid(),
                    EventType = nameof(OrderConfirmedEvent),
                    Payload = System.Text.Json.JsonSerializer.Serialize(new OrderConfirmedEvent
                    {
                        OrderId = orderId, UserId = "u1", UserEmail = "u1@example.com",
                        TotalAmount = 10m, ConfirmedAt = DateTime.UtcNow, Items = []
                    }),
                    CreatedAt = DateTime.UtcNow,
                    TraceContext = OrderSpans.CurrentContext()
                });
                await db.SaveChangesAsync();
            }
        }

        // ... and the delivery loop, much later, with no span of its own
        Activity.Current.Should().BeNull();
        var service = new OutboxDeliveryService(
            provider.GetRequiredService<IServiceScopeFactory>(), readiness, new ListLogger<OutboxDeliveryService>());
        await service.DeliverPendingMessages(CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        (await harness.Consumed.Any<OrderConfirmedEvent>(timeout.Token)).Should().BeTrue();

        var delivery = spans.Named("outbox.deliver").Should().ContainSingle().Subject;
        delivery.TraceId.Should().Be(request.TraceId);
        delivery.ParentSpanId.Should().Be(request.SpanId);
        delivery.GetTagItem("order.id").Should().Be(orderId.ToString());
        seen.Should().ContainSingle().Which.TraceId.Should().Be(request.TraceId.ToHexString(),
            "the consumer that creates the saga continues the request's trace");
    }
}
