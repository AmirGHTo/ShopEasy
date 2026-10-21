using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.ReadModels;
using ShopEasy.OrderService.Sagas;
using ShopEasy.Shared.Contracts;
using ShopEasy.Shared.Resilience;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Infrastructure;

// The saga consumers that send the next command, wired through the MassTransit outbox the way
// Program.cs wires them, against a real PostgreSQL. The command is stored with the new saga state
// and sent after the commit; these tests check that it arrives, and that it is not lost when the
// send right after the commit fails. See InfrastructureFacts.cs.
public class OrderConsumerOutboxPostgresTests
{
    private sealed class PaymentProbe : IConsumer<ProcessPaymentCommand>
    {
        public Task Consume(ConsumeContext<ProcessPaymentCommand> context) => Task.CompletedTask;
    }

    private sealed class CommitProbe : IConsumer<CommitInventoryCommand>
    {
        public Task Consume(ConsumeContext<CommitInventoryCommand> context) => Task.CompletedTask;
    }

    private sealed class ReleaseProbe : IConsumer<ReleaseInventoryCommand>
    {
        public Task Consume(ConsumeContext<ReleaseInventoryCommand> context) => Task.CompletedTask;
    }

    private sealed class RefundProbe : IConsumer<RefundPaymentCommand>
    {
        public Task Consume(ConsumeContext<RefundPaymentCommand> context) => Task.CompletedTask;
    }

    private sealed class RestockProbe : IConsumer<RestockInventoryCommand>
    {
        public Task Consume(ConsumeContext<RestockInventoryCommand> context) => Task.CompletedTask;
    }

    private static ServiceProvider BuildHost(PostgresTestDatabase database, BrokerOutage? outage = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<OrderDbContext>(o => o.UseNpgsql(database.ConnectionString));
        services.AddScoped<ISagaLocking, PostgresSagaLocking>();
        services.AddScoped<OrderReadModelUpdater>();
        services.AddMassTransitTestHarness(x =>
        {
            x.AllowSlowRunners();
            x.AddConsumer<InventoryReservedConsumer>();
            x.AddConsumer<PaymentSucceededConsumer>();
            x.AddConsumer<PaymentFailedConsumer>();
            x.AddConsumer<InventoryCommittedConsumer>();
            x.AddConsumer<InventoryCommitFailedConsumer>();
            x.AddConsumer<PaymentRefundedConsumer>();
            x.AddConsumer<PaymentProbe>();
            x.AddConsumer<CommitProbe>();
            x.AddConsumer<ReleaseProbe>();
            x.AddConsumer<RefundProbe>();
            x.AddConsumer<RestockProbe>();
            x.AddEntityFrameworkOutbox<OrderDbContext>(o =>
            {
                o.UsePostgres();
                o.IsolationLevel = System.Data.IsolationLevel.ReadCommitted;
                // as in Program.cs, where the services outside a consumer use the bus outbox
                o.UseBusOutbox();
                o.QueryDelay = TimeSpan.FromMilliseconds(100);
            });
            x.UsingInMemory((ctx, cfg) =>
            {
                cfg.UseConsumerResilience(TestResilience.FastConsumers());
                if (outage is not null)
                {
                    cfg.ConfigureSend(s => s.UseExecute(outage.BeforeSend));
                }

                cfg.ReceiveEndpoint("order-saga-inventory-reserved", e =>
                {
                    e.UseEntityFrameworkOutbox<OrderDbContext>(ctx);
                    e.ConfigureConsumer<InventoryReservedConsumer>(ctx);
                });
                cfg.ReceiveEndpoint("order-saga-payment-succeeded", e =>
                {
                    e.UseEntityFrameworkOutbox<OrderDbContext>(ctx);
                    e.ConfigureConsumer<PaymentSucceededConsumer>(ctx);
                });
                cfg.ReceiveEndpoint("order-saga-payment-failed", e =>
                {
                    e.UseEntityFrameworkOutbox<OrderDbContext>(ctx);
                    e.ConfigureConsumer<PaymentFailedConsumer>(ctx);
                });
                cfg.ReceiveEndpoint("order-saga-inventory-committed", e =>
                {
                    e.UseEntityFrameworkOutbox<OrderDbContext>(ctx);
                    e.ConfigureConsumer<InventoryCommittedConsumer>(ctx);
                });
                cfg.ReceiveEndpoint("order-saga-inventory-commit-failed", e =>
                {
                    e.UseEntityFrameworkOutbox<OrderDbContext>(ctx);
                    e.ConfigureConsumer<InventoryCommitFailedConsumer>(ctx);
                });
                cfg.ReceiveEndpoint("order-saga-payment-refunded", e =>
                {
                    e.UseEntityFrameworkOutbox<OrderDbContext>(ctx);
                    e.ConfigureConsumer<PaymentRefundedConsumer>(ctx);
                });
                cfg.ReceiveEndpoint("probe-payment", e => e.ConfigureConsumer<PaymentProbe>(ctx));
                cfg.ReceiveEndpoint("probe-commit", e => e.ConfigureConsumer<CommitProbe>(ctx));
                cfg.ReceiveEndpoint("probe-release", e => e.ConfigureConsumer<ReleaseProbe>(ctx));
                cfg.ReceiveEndpoint("probe-refund", e => e.ConfigureConsumer<RefundProbe>(ctx));
                cfg.ReceiveEndpoint("probe-restock", e => e.ConfigureConsumer<RestockProbe>(ctx));
            });
        });
        return services.BuildServiceProvider(true);
    }

    private static async Task<(DbContextOptions<OrderDbContext> Options, Guid OrderId)> SeedAsync(
        PostgresTestDatabase database, SagaStatus status, string? failureReason = null)
    {
        var options = new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(database.ConnectionString).Options;
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var db = new OrderDbContext(options);
        await db.Database.MigrateAsync();
        db.Orders.Add(new Order
        {
            Id = orderId, UserId = "u1", UserEmail = "u1@example.com", Status = OrderStatus.Pending,
            TotalAmount = 25m, CreatedAt = now, UpdatedAt = now
        });
        db.SagaStates.Add(new OrderSagaState
        {
            Id = Guid.NewGuid(), OrderId = orderId, Status = status, UserId = "u1", UserEmail = "u1@example.com",
            TotalAmount = 25m, ItemsJson = "[]", FailureReason = failureReason, CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return (options, orderId);
    }

    private static async Task<SagaStatus> StatusOfAsync(DbContextOptions<OrderDbContext> options, Guid orderId)
    {
        await using var db = new OrderDbContext(options);
        return (await db.SagaStates.SingleAsync(s => s.OrderId == orderId)).Status;
    }

    private static CancellationToken Within(int seconds) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    [RequiresPostgresFact]
    public async Task WhenInventoryIsReserved_TheSagaMovesOn_AndThePaymentCommandIsDelivered()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("orderreserved");
        await using var provider = BuildHost(database);
        var (options, orderId) = await SeedAsync(database, SagaStatus.ReservingInventory);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new InventoryReservedEvent { OrderId = orderId });

        (await harness.Consumed.Any<ProcessPaymentCommand>(x => x.Context.Message.OrderId == orderId, Within(15))).Should().BeTrue();
        (await StatusOfAsync(options, orderId)).Should().Be(SagaStatus.ProcessingPayment);
    }

    [RequiresPostgresFact]
    public async Task WhenPaymentSucceeds_TheSagaMovesOn_AndTheCommitCommandIsDelivered()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("orderpaid");
        await using var provider = BuildHost(database);
        var (options, orderId) = await SeedAsync(database, SagaStatus.ProcessingPayment);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new PaymentSucceededEvent { OrderId = orderId, PaymentId = Guid.NewGuid(), Amount = 25m });

        (await harness.Consumed.Any<CommitInventoryCommand>(x => x.Context.Message.OrderId == orderId, Within(15))).Should().BeTrue();
        (await StatusOfAsync(options, orderId)).Should().Be(SagaStatus.Completing);
    }

    [RequiresPostgresFact]
    public async Task WhenPaymentFails_TheSagaMovesOn_AndTheReleaseCommandIsDelivered()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("orderfailed");
        await using var provider = BuildHost(database);
        var (options, orderId) = await SeedAsync(database, SagaStatus.ProcessingPayment);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new PaymentFailedEvent { OrderId = orderId, Reason = "declined" });

        (await harness.Consumed.Any<ReleaseInventoryCommand>(x => x.Context.Message.OrderId == orderId, Within(15))).Should().BeTrue();
        (await StatusOfAsync(options, orderId)).Should().Be(SagaStatus.ReleasingInventory);
    }

    [RequiresPostgresFact]
    public async Task APaymentThatArrivesTooLate_IsRefunded_WithoutMovingTheSaga()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("orderlate");
        await using var provider = BuildHost(database);
        var (options, orderId) = await SeedAsync(database, SagaStatus.Cancelled);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new PaymentSucceededEvent { OrderId = orderId, PaymentId = Guid.NewGuid(), Amount = 25m });

        (await harness.Consumed.Any<RefundPaymentCommand>(x => x.Context.Message.OrderId == orderId, Within(15))).Should().BeTrue();
        (await StatusOfAsync(options, orderId)).Should().Be(SagaStatus.Cancelled);
    }

    [RequiresPostgresFact]
    public async Task WhenTheStockCommitFails_TheSagaFails_AndTheRefundCommandIsDelivered()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("ordercommitfailed");
        await using var provider = BuildHost(database);
        var (options, orderId) = await SeedAsync(database, SagaStatus.Completing);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new InventoryCommitFailedEvent { OrderId = orderId, Reason = "reservation missing" });

        (await harness.Consumed.Any<RefundPaymentCommand>(x => x.Context.Message.OrderId == orderId, Within(15))).Should().BeTrue();
        (await StatusOfAsync(options, orderId)).Should().Be(SagaStatus.Failed);
    }

    [RequiresPostgresFact]
    public async Task WhenTheStockIsCommittedForAnOrderCancelledMeanwhile_TheRefundCommandIsDelivered()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("ordercancelledmeanwhile");
        await using var provider = BuildHost(database);
        var (options, orderId) = await SeedAsync(database, SagaStatus.Completing, FailureReasons.CancelledByCustomer);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new InventoryCommittedEvent { OrderId = orderId });

        (await harness.Consumed.Any<RefundPaymentCommand>(x => x.Context.Message.OrderId == orderId, Within(15))).Should().BeTrue();
        (await StatusOfAsync(options, orderId)).Should().Be(SagaStatus.RefundingPayment);
    }

    [RequiresPostgresFact]
    public async Task WhenThePaymentIsRefunded_TheSagaMovesOn_AndTheRestockCommandIsDelivered()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("orderrefunded");
        await using var provider = BuildHost(database);
        var (options, orderId) = await SeedAsync(database, SagaStatus.RefundingPayment);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new PaymentRefundedEvent { OrderId = orderId, PaymentId = Guid.NewGuid(), Amount = 25m });

        (await harness.Consumed.Any<RestockInventoryCommand>(x => x.Context.Message.OrderId == orderId, Within(15))).Should().BeTrue();
        (await StatusOfAsync(options, orderId)).Should().Be(SagaStatus.RestockingInventory);
    }

    // The gap this closes: the state was committed, then the process or the broker failed before
    // the command left, and only the saga timeout (two minutes) put it right. Now the command is
    // in the database with the state, so the redelivered event sends it.
    [RequiresPostgresFact]
    public async Task WhenTheSendRightAfterTheCommitFails_TheCommandIsStillDelivered()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("orderoutage");
        var outage = new BrokerOutage(nameof(CommitInventoryCommand));
        await using var provider = BuildHost(database, outage);
        var (options, orderId) = await SeedAsync(database, SagaStatus.ProcessingPayment);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        outage.ReadSaga = () => StatusOfAsync(options, orderId).GetAwaiter().GetResult();
        outage.FailNext(1);

        await harness.Bus.Publish(new PaymentSucceededEvent { OrderId = orderId, PaymentId = Guid.NewGuid(), Amount = 25m });

        (await harness.Consumed.Any<CommitInventoryCommand>(x => x.Context.Message.OrderId == orderId, Within(20))).Should().BeTrue();
        outage.Injected.Should().Be(1, "the first send of the command was refused");
        outage.SagaWhenItFailed.Should().Be(SagaStatus.Completing, "the new state was already committed when the send failed");
        (await StatusOfAsync(options, orderId)).Should().Be(SagaStatus.Completing);
    }
}
