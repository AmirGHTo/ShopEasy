using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShopEasy.PaymentService.Consumers;
using ShopEasy.PaymentService.Data;
using ShopEasy.Shared.Contracts;
using ShopEasy.Shared.Resilience;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Infrastructure;

// Payment consumer against a real PostgreSQL, wired through the MassTransit transactional
// outbox the way Program.cs wires it. See InfrastructureFacts.cs.
public class PaymentPostgresTests
{
    private sealed class SucceededProbe : IConsumer<PaymentSucceededEvent>
    {
        public Task Consume(ConsumeContext<PaymentSucceededEvent> context) => Task.CompletedTask;
    }

    private sealed class RefundedProbe : IConsumer<PaymentRefundedEvent>
    {
        public Task Consume(ConsumeContext<PaymentRefundedEvent> context) => Task.CompletedTask;
    }

    private static ServiceProvider BuildHost(PostgresTestDatabase database, FaultInjector? injector = null, int transientRetryLimit = 12)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Testing:ForcePaymentSuccess"] = "true" })
            .Build());
        services.AddDbContext<PaymentDbContext>(o =>
        {
            o.UseNpgsql(database.ConnectionString);
            if (injector is not null) o.AddInterceptors(injector);
        });
        services.AddMassTransitTestHarness(x =>
        {
            x.AllowSlowRunners();
            x.AddConsumer<ProcessPaymentConsumer>();
            x.AddConsumer<SucceededProbe>();
            x.AddConsumer<RefundPaymentConsumer>();
            x.AddConsumer<RefundedProbe>();
            x.AddEntityFrameworkOutbox<PaymentDbContext>(o =>
            {
                o.UsePostgres();
                o.IsolationLevel = System.Data.IsolationLevel.ReadCommitted;
            });
            x.UsingInMemory((ctx, cfg) =>
            {
                // As in Program.cs: a message that loses a race (for example a unique-key conflict
                // with its twin) is retried and then takes the "already processed" path, and a
                // database outage is ridden out instead of dead-lettering the message
                cfg.UseConsumerResilience(TestResilience.FastConsumers(transientRetryLimit));
                cfg.ReceiveEndpoint("payment-process", e =>
                {
                    e.UseEntityFrameworkOutbox<PaymentDbContext>(ctx);
                    e.ConfigureConsumer<ProcessPaymentConsumer>(ctx);
                });
                cfg.ReceiveEndpoint("payment-refund", e =>
                {
                    e.UseEntityFrameworkOutbox<PaymentDbContext>(ctx);
                    e.ConfigureConsumer<RefundPaymentConsumer>(ctx);
                });
                cfg.ReceiveEndpoint("probe-succeeded", e => e.ConfigureConsumer<SucceededProbe>(ctx));
                cfg.ReceiveEndpoint("probe-refunded", e => e.ConfigureConsumer<RefundedProbe>(ctx));
            });
        });
        return services.BuildServiceProvider(true);
    }

    private static ProcessPaymentCommand Command(Guid orderId) => new()
    {
        OrderId = orderId, UserId = "u1", UserEmail = "u1@example.com",
        Currency = "USD", Amount = 49.90m
    };

    [RequiresPostgresFact]
    public async Task ProcessPayment_SavesThePaymentAndDeliversTheResultEvent()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("pay");
        await using var provider = BuildHost(database);
        var options = new DbContextOptionsBuilder<PaymentDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using (var setup = new PaymentDbContext(options)) await setup.Database.MigrateAsync();

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var orderId = Guid.NewGuid();

        await harness.Bus.Publish(Command(orderId));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        (await harness.Consumed.Any<PaymentSucceededEvent>(
            x => x.Context.Message.OrderId == orderId, timeout.Token)).Should().BeTrue();

        await using var check = new PaymentDbContext(options);
        var payment = await check.Payments.SingleAsync(p => p.OrderId == orderId);
        payment.Status.Should().Be(PaymentStatus.Succeeded);
        payment.Amount.Should().Be(49.90m);
        (await check.ProcessedEvents.CountAsync(e => e.EventId == orderId)).Should().Be(1);
    }

    [RequiresPostgresFact]
    public async Task ARefund_MarksThePaymentRefunded_AndDeliversTheRefundedEvent()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("payrefund");
        await using var provider = BuildHost(database);
        var options = new DbContextOptionsBuilder<PaymentDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using (var setup = new PaymentDbContext(options)) await setup.Database.MigrateAsync();

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var orderId = Guid.NewGuid();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await harness.Bus.Publish(Command(orderId));
        (await harness.Consumed.Any<PaymentSucceededEvent>(x => x.Context.Message.OrderId == orderId, timeout.Token)).Should().BeTrue();

        await harness.Bus.Publish(new RefundPaymentCommand { OrderId = orderId, Reason = "cancelled" });

        (await harness.Consumed.Any<PaymentRefundedEvent>(
            x => x.Context.Message.OrderId == orderId && x.Context.Message.Amount == 49.90m, timeout.Token)).Should().BeTrue();
        await using var check = new PaymentDbContext(options);
        (await check.Payments.SingleAsync(p => p.OrderId == orderId)).Status.Should().Be(PaymentStatus.Refunded);
    }

    [RequiresPostgresFact]
    public async Task TheSameCommandSentTwice_ChargesOnce_AndStillConfirmsTheResult()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("paydup");
        await using var provider = BuildHost(database);
        var options = new DbContextOptionsBuilder<PaymentDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using (var setup = new PaymentDbContext(options)) await setup.Database.MigrateAsync();

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var orderId = Guid.NewGuid();

        // two distinct messages (new MessageId each) for the same order, as a saga retry sends
        await harness.Bus.Publish(Command(orderId));
        await harness.Bus.Publish(Command(orderId));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested
               && harness.Consumed.Select<PaymentSucceededEvent>().Count() < 2)
            await Task.Delay(100);

        harness.Consumed.Select<PaymentSucceededEvent>().Count().Should().Be(2);

        await using var check = new PaymentDbContext(options);
        (await check.Payments.CountAsync(p => p.OrderId == orderId)).Should().Be(1);
    }

    // The database drops the connection six times in a row while a payment is being processed
    // (more than the three retries the service used to allow). The message must be processed once
    // the database is back: one charge, one idempotency row, one result event, and no trip to the
    // error queue.
    [RequiresPostgresFact]
    public async Task ADatabaseOutageWhileCharging_IsRiddenOut_AndTheOrderIsChargedExactlyOnce()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("payoutage");
        var injector = new FaultInjector();
        await using var provider = BuildHost(database, injector);
        var options = new DbContextOptionsBuilder<PaymentDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using (var setup = new PaymentDbContext(options)) await setup.Database.MigrateAsync();

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var orderId = Guid.NewGuid();

        injector.FailNext(6);
        await harness.Bus.Publish(Command(orderId));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (await harness.Consumed.Any<PaymentSucceededEvent>(
            x => x.Context.Message.OrderId == orderId, timeout.Token)).Should().BeTrue();
        await Task.Delay(300); // a duplicate delivery, if there were one, would arrive by now

        injector.Injected.Should().Be(6, "the outage really happened");
        var attempts = harness.Consumed.Select<ProcessPaymentCommand>().ToList();
        attempts.Should().ContainSingle().Which.Exception.Should().BeNull("the message was never given up on");
        harness.Consumed.Select<PaymentSucceededEvent>().Count().Should().Be(1);

        await using var check = new PaymentDbContext(options);
        (await check.Payments.CountAsync(p => p.OrderId == orderId)).Should().Be(1);
        (await check.ProcessedEvents.CountAsync(e => e.EventId == orderId)).Should().Be(1);
    }

    // An outage that outlasts the patient retries: the message is given up on after exactly the
    // configured number of attempts (so it can be seen in the error queue and requeued) and
    // nothing of it is left half-written.
    [RequiresPostgresFact]
    public async Task AnOutageLongerThanThePatientRetries_EndsInTheErrorQueue_WithNothingHalfWritten()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("paydown");
        var injector = new FaultInjector();
        await using var provider = BuildHost(database, injector, transientRetryLimit: 4);
        var options = new DbContextOptionsBuilder<PaymentDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using (var setup = new PaymentDbContext(options)) await setup.Database.MigrateAsync();

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var orderId = Guid.NewGuid();

        // the first attempt and the four patient retries all hit the outage; what follows (the
        // fault bookkeeping) finds the database back
        injector.FailNext(5);
        await harness.Bus.Publish(Command(orderId));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // the pipeline finished with the failure (the message goes to the error queue)
        (await harness.Consumed.Any<ProcessPaymentCommand>(x => x.Exception is not null, timeout.Token))
            .Should().BeTrue();

        injector.Injected.Should().Be(5);
        await using var check = new PaymentDbContext(options);
        (await check.Payments.CountAsync()).Should().Be(0);
        (await check.ProcessedEvents.CountAsync()).Should().Be(0);
    }

    [RequiresPostgresFact]
    public async Task UnitOfWork_OpensItsOwnTransaction_WhenNoneIsActive()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("payuow");
        var options = new DbContextOptionsBuilder<PaymentDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using var db = new PaymentDbContext(options);
        await db.Database.MigrateAsync();

        await using (var own = await db.Database.BeginOrJoinAsync())
        {
            own.OwnsTransaction.Should().BeTrue();
            await own.RollbackAsync();
        }

        await using var outer = await db.Database.BeginTransactionAsync();
        await using var joined = await db.Database.BeginOrJoinAsync();
        joined.OwnsTransaction.Should().BeFalse();
    }
}
