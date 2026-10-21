using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.ReadModels;
using ShopEasy.OrderService.Sagas;
using ShopEasy.OrderService.Services;
using ShopEasy.Shared.Contracts;
using ShopEasy.Shared.Messaging;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Infrastructure;

// What the Order service publishes outside a consumer (the orchestrator, the saga timeout service,
// the cancel endpoint) goes through the MassTransit bus outbox, wired the way Program.cs wires it,
// against a real PostgreSQL: the command is saved by the same SaveChanges as the saga state and a
// delivery service sends it afterwards. See InfrastructureFacts.cs.
public class OrderBusOutboxPostgresTests
{
    private sealed class ReserveProbe : IConsumer<ReserveInventoryCommand>
    {
        public Task Consume(ConsumeContext<ReserveInventoryCommand> context) => Task.CompletedTask;
    }

    private sealed class ReleaseProbe : IConsumer<ReleaseInventoryCommand>
    {
        public Task Consume(ConsumeContext<ReleaseInventoryCommand> context) => Task.CompletedTask;
    }

    private sealed class ConfirmedProbe : IConsumer<OrderConfirmedEvent>
    {
        public Task Consume(ConsumeContext<OrderConfirmedEvent> context) => Task.CompletedTask;
    }

    private sealed class Host : IAsyncDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required ITestHarness Harness { get; init; }
        public required DbContextOptions<OrderDbContext> Options { get; init; }

        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private static async Task<Host> StartAsync(
        PostgresTestDatabase database, BrokerOutage? outage = null, bool outboxDelivery = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<OrderDbContext>(o => o.UseNpgsql(database.ConnectionString));
        services.AddScoped<ISagaLocking, PostgresSagaLocking>();
        services.AddScoped<OrderReadModelUpdater>();
        services.AddScoped<OrderCancellationService>();
        services.AddSingleton(Substitute.For<IBrokerReadiness>());
        services.AddMassTransitTestHarness(x =>
        {
            x.AllowSlowRunners();
            x.AddConsumer<ReserveProbe>();
            x.AddConsumer<ReleaseProbe>();
            x.AddConsumer<ConfirmedProbe>();
            x.AddEntityFrameworkOutbox<OrderDbContext>(o =>
            {
                o.UsePostgres();
                o.IsolationLevel = System.Data.IsolationLevel.ReadCommitted;
                o.UseBusOutbox(b =>
                {
                    if (!outboxDelivery) b.DisableDeliveryService();
                });
                o.QueryDelay = TimeSpan.FromMilliseconds(100);
            });
            x.UsingInMemory((ctx, cfg) =>
            {
                if (outage is not null) cfg.ConfigureSend(s => s.UseExecute(outage.BeforeSend));
                cfg.ReceiveEndpoint("probe-reserve", e => e.ConfigureConsumer<ReserveProbe>(ctx));
                cfg.ReceiveEndpoint("probe-release", e => e.ConfigureConsumer<ReleaseProbe>(ctx));
                cfg.ReceiveEndpoint("probe-confirmed", e => e.ConfigureConsumer<ConfirmedProbe>(ctx));
            });
        });
        var provider = services.BuildServiceProvider(true);

        var options = new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using (var setup = new OrderDbContext(options)) await setup.Database.MigrateAsync();

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        return new Host { Provider = provider, Harness = harness, Options = options };
    }

    private static async Task<Guid> SeedAsync(
        DbContextOptions<OrderDbContext> options, SagaStatus status, DateTime? updatedAt = null)
    {
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var db = new OrderDbContext(options);
        db.Orders.Add(new Order
        {
            Id = orderId, UserId = "u1", UserEmail = "u1@example.com", Status = OrderStatus.Pending,
            TotalAmount = 25m, CreatedAt = now, UpdatedAt = now
        });
        db.OrderSummaries.Add(new OrderSummary
        {
            OrderId = orderId, UserId = "u1", UserEmail = "u1@example.com", StatusName = "Pending",
            StatusCode = (int)OrderStatus.Pending, TotalAmount = 25m, AmountDisplay = "$25.00", ItemCount = 1,
            ItemsSummary = "Widget x1", PlacedAt = now, UpdatedAt = now
        });
        db.SagaStates.Add(new OrderSagaState
        {
            Id = Guid.NewGuid(), OrderId = orderId, Status = status, UserId = "u1", UserEmail = "u1@example.com",
            TotalAmount = 25m, ItemsJson = "[]", CreatedAt = updatedAt ?? now, UpdatedAt = updatedAt ?? now
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    private static async Task<OrderSagaState> SagaAsync(DbContextOptions<OrderDbContext> options, Guid orderId)
    {
        await using var db = new OrderDbContext(options);
        return await db.SagaStates.AsNoTracking().SingleAsync(s => s.OrderId == orderId);
    }

    private static CancellationToken Within(int seconds) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    [RequiresPostgresFact]
    public async Task TheOrchestrator_SavesTheNewStateWithTheCommand_AndTheCommandIsDelivered()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("busoutstart");
        await using var host = await StartAsync(database);
        var orderId = await SeedAsync(host.Options, SagaStatus.Started);

        await new SagaOrchestrator(host.Provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<SagaOrchestrator>.Instance)
            .ProcessPendingSagas(CancellationToken.None);

        (await host.Harness.Consumed.Any<ReserveInventoryCommand>(x => x.Context.Message.OrderId == orderId, Within(15))).Should().BeTrue();
        (await SagaAsync(host.Options, orderId)).Status.Should().Be(SagaStatus.ReservingInventory);
    }

    [RequiresPostgresFact]
    public async Task WhenTheBrokerRefusesTheFirstSends_TheCommandWaitsInTheTable_AndIsDeliveredWhenItRecovers()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("busoutoutage");
        var outage = new BrokerOutage(nameof(ReserveInventoryCommand));
        await using var host = await StartAsync(database, outage);
        var orderId = await SeedAsync(host.Options, SagaStatus.Started);
        outage.ReadSaga = () => SagaAsync(host.Options, orderId).GetAwaiter().GetResult().Status;
        outage.FailNext(2);

        await new SagaOrchestrator(host.Provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<SagaOrchestrator>.Instance)
            .ProcessPendingSagas(CancellationToken.None);

        (await host.Harness.Consumed.Any<ReserveInventoryCommand>(x => x.Context.Message.OrderId == orderId, Within(20))).Should().BeTrue();
        outage.Injected.Should().Be(2);
        outage.SagaWhenItFailed.Should().Be(SagaStatus.ReservingInventory, "the state was committed before the first send");
        var saga = await SagaAsync(host.Options, orderId);
        saga.Status.Should().Be(SagaStatus.ReservingInventory);
        saga.RetryCount.Should().Be(0, "the orchestrator had nothing to retry");
    }

    [RequiresPostgresFact]
    public async Task TheCancelEndpoint_SavesTheCancellationWithTheReleaseCommand_AndTheCommandIsDelivered()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("busoutcancel");
        await using var host = await StartAsync(database);
        var orderId = await SeedAsync(host.Options, SagaStatus.ProcessingPayment);

        using (var scope = host.Provider.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<OrderCancellationService>()
                .CancelAsync(orderId, "u1", isAdmin: false);
            result.Outcome.Should().Be(CancelOutcome.Cancelling);
        }

        (await host.Harness.Consumed.Any<ReleaseInventoryCommand>(x => x.Context.Message.OrderId == orderId, Within(15))).Should().BeTrue();
        (await SagaAsync(host.Options, orderId)).Status.Should().Be(SagaStatus.ReleasingInventory);
    }

    [RequiresPostgresFact]
    public async Task TheTimeoutService_ResendsALostCommand_ThroughTheOutbox()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("busouttimeout");
        await using var host = await StartAsync(database);
        var orderId = await SeedAsync(host.Options, SagaStatus.ReleasingInventory, DateTime.UtcNow.AddMinutes(-5));

        using (var scope = host.Provider.CreateScope())
            await new SagaTimeoutService(Substitute.For<IServiceScopeFactory>(), NullLogger<SagaTimeoutService>.Instance)
                .CheckForTimedOutSagasPublic(
                    scope.ServiceProvider.GetRequiredService<OrderDbContext>(),
                    scope.ServiceProvider.GetRequiredService<IPublishEndpoint>(),
                    scope.ServiceProvider.GetRequiredService<ISagaLocking>(),
                    CancellationToken.None);

        (await host.Harness.Consumed.Any<ReleaseInventoryCommand>(x => x.Context.Message.OrderId == orderId, Within(15))).Should().BeTrue();
        (await SagaAsync(host.Options, orderId)).RetryCount.Should().Be(1);
    }

    // The service has its own outbox table (OutboxEntries) with its own retries, and publishes to the
    // bus itself. The scoped IPublishEndpoint would hand the event to the MassTransit outbox instead;
    // with that delivery service not running here, the event arrives only if it went to the bus.
    [RequiresPostgresFact]
    public async Task TheOrderConfirmedDelivery_StillPublishesStraightToTheBus()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("busoutconfirmed");
        await using var host = await StartAsync(database, outboxDelivery: false);
        var orderId = Guid.NewGuid();
        await using (var db = new OrderDbContext(host.Options))
        {
            db.OutboxEntries.Add(new OutboxEntry
            {
                Id = Guid.NewGuid(), EventType = nameof(OrderConfirmedEvent), CreatedAt = DateTime.UtcNow,
                Payload = System.Text.Json.JsonSerializer.Serialize(new OrderConfirmedEvent
                {
                    OrderId = orderId, UserId = "u1", UserEmail = "u1@example.com", TotalAmount = 10m,
                    ConfirmedAt = DateTime.UtcNow, Items = []
                })
            });
            await db.SaveChangesAsync();
        }
        host.Provider.GetRequiredService<IBrokerReadiness>().IsReadyAsync(Arg.Any<CancellationToken>()).Returns(true);

        await new OutboxDeliveryService(
                host.Provider.GetRequiredService<IServiceScopeFactory>(),
                host.Provider.GetRequiredService<IBrokerReadiness>(),
                new ListLogger<OutboxDeliveryService>())
            .DeliverPendingMessages(CancellationToken.None);

        (await host.Harness.Consumed.Any<OrderConfirmedEvent>(x => x.Context.Message.OrderId == orderId, Within(15))).Should().BeTrue();
        await using var check = new OrderDbContext(host.Options);
        (await check.OutboxEntries.SingleAsync()).DeliveredAt.Should().NotBeNull();
        (await check.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>().CountAsync()).Should().Be(0, "the event did not go through the MassTransit outbox");
    }
}
