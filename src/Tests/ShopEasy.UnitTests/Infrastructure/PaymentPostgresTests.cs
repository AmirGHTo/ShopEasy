using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShopEasy.PaymentService.Consumers;
using ShopEasy.PaymentService.Data;
using ShopEasy.Shared.Contracts;
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

    private static ServiceProvider BuildHost(PostgresTestDatabase database)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Testing:ForcePaymentSuccess"] = "true" })
            .Build());
        services.AddDbContext<PaymentDbContext>(o => o.UseNpgsql(database.ConnectionString));
        services.AddMassTransitTestHarness(x =>
        {
            x.AllowSlowRunners();
            x.AddConsumer<ProcessPaymentConsumer>();
            x.AddConsumer<SucceededProbe>();
            x.AddEntityFrameworkOutbox<PaymentDbContext>(o =>
            {
                o.UsePostgres();
                o.IsolationLevel = System.Data.IsolationLevel.ReadCommitted;
            });
            x.UsingInMemory((ctx, cfg) =>
            {
                // As in Program.cs: a message that loses a race (for example a unique-key conflict
                // with its twin) is retried and then takes the "already processed" path
                cfg.UseMessageRetry(r => r.Immediate(3));
                cfg.ReceiveEndpoint("payment-process", e =>
                {
                    e.UseEntityFrameworkOutbox<PaymentDbContext>(ctx);
                    e.ConfigureConsumer<ProcessPaymentConsumer>(ctx);
                });
                cfg.ReceiveEndpoint("probe-succeeded", e => e.ConfigureConsumer<SucceededProbe>(ctx));
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
