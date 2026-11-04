using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Metrics;
using ShopEasy.OrderService.ReadModels;
using ShopEasy.OrderService.Sagas;
using ShopEasy.PaymentService.Consumers;
using ShopEasy.PaymentService.Data;
using ShopEasy.PaymentService.Metrics;
using ShopEasy.Shared.Contracts;
using ShopEasy.Shared.Telemetry;
using ShopEasy.UnitTests.Sagas;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Observability;

// The synthetic probe buys something and gives it back every minute. That is real work for the system and
// must stay visible in latencies, queues and traces, but it must not be counted as business: the orders,
// the sales and the payments of its account are left out of those counters, and only those.
// SyntheticTraffic is process-wide state, so these tests run alone (the Metrics collection) and put it back.
[Collection(MetricsCollection.Name)]
public class SyntheticTrafficTests : IDisposable
{
    private const string Probe = "probe@synthetic.test";
    private const string Customer = "customer@example.com";

    private const string Placed = "shopeasy_orders_placed_total{status=\"accepted\"}";
    private const string Amounts = "shopeasy_order_amount_dollars_count";
    private const string Sales = "shopeasy_order_outcomes_total{outcome=\"confirmed\",reason=\"completed\"}";
    private const string Revenue = "shopeasy_orders_confirmed_amount_total";
    private const string Cancelled = "shopeasy_order_outcomes_total{outcome=\"cancelled\",reason=\"customer\"}";
    private const string Failed = "shopeasy_order_outcomes_total{outcome=\"failed\",reason=\"orchestrator_error\"}";
    private const string Refused = "shopeasy_orders_rejected_total{reason=\"invalid_request\"}";
    private const string Succeeded = "shopeasy_payments_total{result=\"succeeded\"}";
    private const string Declined = "shopeasy_payments_total{result=\"declined\"}";
    private const string Refunded = "shopeasy_payments_total{result=\"refunded\"}";
    private const string RefundedAmount = "shopeasy_payments_amount_total{result=\"refunded\"}";

    public SyntheticTrafficTests() => SyntheticTraffic.Configure(Probe);

    public void Dispose() => SyntheticTraffic.Configure((string?)null);

    // ── who is the probe ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(Probe, true)]
    [InlineData("PROBE@Synthetic.TEST", true)]
    [InlineData("  probe@synthetic.test ", true)]
    [InlineData(Customer, false)]
    [InlineData("xprobe@synthetic.test", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheProbe_IsTheConfiguredAddress_WithoutRegardToCaseOrSpaces(string? email, bool expected)
        => SyntheticTraffic.IsProbe(email).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutAProbeAddress_NobodyIsTheProbe(string? setting)
    {
        SyntheticTraffic.Configure(setting);

        SyntheticTraffic.IsProbe(Probe).Should().BeFalse();
        SyntheticTraffic.IsProbe(null).Should().BeFalse();
    }

    [Fact]
    public void TheAddressComesFromTheSetting_SyntheticProbeEmail()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Synthetic:ProbeEmail"] = "other@synthetic.test" }).Build();

        SyntheticTraffic.Configure(configuration);

        SyntheticTraffic.IsProbe("other@synthetic.test").Should().BeTrue();
        SyntheticTraffic.IsProbe(Probe).Should().BeFalse();
    }

    // ── the Order service's counters ──────────────────────────────────────────

    private static async Task<double[]> ReadAsync(params string[] series)
    {
        var values = new double[series.Length];
        for (var i = 0; i < series.Length; i++)
            values[i] = await MetricValues.ReadAsync(series[i]);
        return values;
    }

    private static readonly string[] OrderSeries = [Placed, Amounts, Sales, Revenue, Cancelled, Failed, Refused];

    private static void EveryOrderCounter(string? email)
    {
        OrderMetrics.RecordOrderPlaced(25m, email);
        OrderMetrics.RecordOrderConfirmed(25m, email);
        OrderMetrics.RecordOrderCancelled(CancelReason.Customer, email);
        OrderMetrics.RecordOrderFailed(FailReason.OrchestratorError, email);
        OrderMetrics.RecordOrderRejected(OrderRejection.InvalidRequest, email);
    }

    [Fact]
    public async Task TheOrdersOfTheProbe_AreInNoBusinessCounterOfTheOrderService()
    {
        var before = await ReadAsync(OrderSeries);

        EveryOrderCounter(Probe);

        (await ReadAsync(OrderSeries)).Should().Equal(before);
    }

    [Fact]
    public async Task TheOrdersOfACustomer_AreCountedAsBefore()
    {
        var before = await ReadAsync(OrderSeries);

        EveryOrderCounter(Customer);

        var after = await ReadAsync(OrderSeries);
        // placed, amounts, sales, revenue (by 25), cancelled, failed, refused
        after.Zip(before, (a, b) => a - b).Should().Equal(1, 1, 1, 25, 1, 1, 1);
    }

    [Fact]
    public async Task AConfirmedOrderOfTheProbe_IsNotASale_ThroughTheRealConsumer()
    {
        var (sales, revenue) = (await MetricValues.ReadAsync(Sales), await MetricValues.ReadAsync(Revenue));

        await ConfirmAnOrderOf(Probe, 80m);
        (await MetricValues.ReadAsync(Sales)).Should().Be(sales, "the probe's order is not a sale");
        (await MetricValues.ReadAsync(Revenue)).Should().Be(revenue);

        await ConfirmAnOrderOf(Customer, 80m);
        (await MetricValues.ReadAsync(Sales)).Should().Be(sales + 1, "another customer's order is");
        (await MetricValues.ReadAsync(Revenue)).Should().Be(revenue + 80);
    }

    private static async Task ConfirmAnOrderOf(string email, decimal amount)
    {
        using var db = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        var orderId = Guid.NewGuid();
        db.SagaStates.Add(new OrderSagaState
        {
            Id = Guid.NewGuid(), OrderId = orderId, Status = SagaStatus.Completing, UserId = "u", UserEmail = email,
            TotalAmount = amount, ItemsJson = "[]", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var consumer = new InventoryCommittedConsumer(db, new InMemorySagaLocking(),
            new OrderReadModelUpdater(db, NullLogger<OrderReadModelUpdater>.Instance), NullLogger<InventoryCommittedConsumer>.Instance);
        var context = Substitute.For<ConsumeContext<InventoryCommittedEvent>>();
        context.Message.Returns(new InventoryCommittedEvent { OrderId = orderId });

        await consumer.Consume(context);
    }

    // ── the Payment service's counters ────────────────────────────────────────

    [Fact]
    public async Task ThePaymentsOfTheProbe_AreInNoBusinessCounterOfThePaymentService()
    {
        var before = await ReadAsync(Succeeded, Declined, Refunded, RefundedAmount);

        PaymentMetrics.RecordSucceeded(10m, Probe);
        PaymentMetrics.RecordDeclined(10m, Probe);
        PaymentMetrics.RecordRefunded(10m, Probe);

        (await ReadAsync(Succeeded, Declined, Refunded, RefundedAmount)).Should().Equal(before);
    }

    [Fact]
    public async Task ThePaymentsOfACustomer_AreCountedAsBefore()
    {
        var before = await ReadAsync(Succeeded, Declined, Refunded, RefundedAmount);

        PaymentMetrics.RecordSucceeded(10m, Customer);
        PaymentMetrics.RecordDeclined(10m, Customer);
        PaymentMetrics.RecordRefunded(10m, Customer);

        (await ReadAsync(Succeeded, Declined, Refunded, RefundedAmount)).Zip(before, (a, b) => a - b).Should().Equal(1, 1, 1, 10);
    }

    [Theory]
    [InlineData(Probe, 0)]
    [InlineData(Customer, 1)]
    [InlineData(null, 1)] // a sender that does not say whose order it is (an older Order service) is a customer
    public async Task ARefund_IsCountedUnlessItBelongsToTheProbe_ThroughTheRealConsumer(string? email, int counted)
    {
        using var db = new PaymentDbContext(new DbContextOptionsBuilder<PaymentDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        var orderId = Guid.NewGuid();
        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(), OrderId = orderId, UserId = "u", Amount = 42.5m, Status = PaymentStatus.Succeeded,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var before = await ReadAsync(Refunded, RefundedAmount);
        var context = Substitute.For<ConsumeContext<RefundPaymentCommand>>();
        context.Message.Returns(new RefundPaymentCommand { OrderId = orderId, UserEmail = email, Reason = "cancelled" });

        await new RefundPaymentConsumer(db, NullLogger<RefundPaymentConsumer>.Instance).Consume(context);

        (await db.Payments.SingleAsync()).Status.Should().Be(PaymentStatus.Refunded, "the refund itself happens for everyone");
        var after = await ReadAsync(Refunded, RefundedAmount);
        after[0].Should().Be(before[0] + counted);
        after[1].Should().Be(before[1] + counted * 42.5);
    }
}
