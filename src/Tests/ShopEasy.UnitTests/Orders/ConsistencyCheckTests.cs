using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using ShopEasy.OrderService.Data;
using ShopEasy.OrderService.Services;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

// The check that compares settled orders with what Payment holds: which orders it looks at, when a
// mismatch counts, and what it reports.
[Collection(MetricsCollection.Name)]
public class ConsistencyCheckTests : IDisposable
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    // Payment, answering from what the test says it holds; remembers what it was asked
    private sealed class FakePayment : IPaymentAuditClient
    {
        public Dictionary<Guid, List<PaymentRow>> Holds { get; } = new();
        public List<Guid> Asked { get; } = [];
        public Exception? Failure { get; set; }

        public Task<IReadOnlyList<PaymentRow>> GetPaymentsAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken ct = default)
        {
            if (Failure is not null) throw Failure;
            Asked.AddRange(orderIds);
            return Task.FromResult<IReadOnlyList<PaymentRow>>(orderIds.SelectMany(id => Holds.GetValueOrDefault(id) ?? []).ToList());
        }
    }

    private readonly OrderDbContext _db = new(new DbContextOptionsBuilder<OrderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
    private readonly ManualClock _clock = new();
    private readonly FakePayment _payment = new();
    private readonly ListLogger<ConsistencyCheck> _log = new();

    public void Dispose() => _db.Dispose();

    private ConsistencyCheck Check(ConsistencyCheckOptions? options = null) => new(
        Substitute.For<IServiceScopeFactory>(), Options.Create(options ?? new ConsistencyCheckOptions()), _log, clock: _clock);

    private Task Pass(ConsistencyCheck check) => check.CheckAsync(_db, _payment, CancellationToken.None);

    // An order that settled this long ago, with the payment Payment holds for it
    private async Task<Guid> Settled(TimeSpan ago, OrderStatus status = OrderStatus.Confirmed, decimal total = 100m,
        params (string Status, decimal Amount)[] payments)
    {
        var id = Guid.NewGuid();
        _db.Orders.Add(new Order
        {
            Id = id, UserId = "u", UserEmail = "u@test.com", Status = status, TotalAmount = total,
            CreatedAt = _clock.GetUtcNow().UtcDateTime - ago - TimeSpan.FromMinutes(1), UpdatedAt = _clock.GetUtcNow().UtcDateTime - ago
        });
        await _db.SaveChangesAsync();
        _payment.Holds[id] = payments.Select(p => new PaymentRow(id, p.Status, p.Amount)).ToList();
        return id;
    }

    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5); // settled, within the lookback

    private static Task<double> Violations(string rule) =>
        MetricValues.ReadAsync($"shopeasy_consistency_violations{{rule=\"{rule}\"}}");

    [Fact]
    public async Task AnOrderThatAgreesWithItsPayment_IsCheckedAndCausesNothing()
    {
        var order = await Settled(Fresh, payments: ("Succeeded", 100m));
        var checkedBefore = await MetricValues.ReadAsync("shopeasy_consistency_orders_checked_total");

        await Pass(Check());

        _payment.Asked.Should().Equal(order);
        (await MetricValues.ReadAsync("shopeasy_consistency_orders_checked_total")).Should().Be(checkedBefore + 1);
        foreach (var rule in ConsistencyRules.All)
            (await Violations(rule)).Should().Be(0);
        _log.Messages(LogLevel.Error).Should().BeEmpty();
    }

    [Fact]
    public async Task AnOrderThatChangedJustNow_IsLeftAloneUntilItHasSettled()
    {
        await Settled(TimeSpan.FromSeconds(30), payments: ("Succeeded", 100m));
        var check = Check();

        await Pass(check);
        _payment.Asked.Should().BeEmpty("a refund or a late payment may still be on its way");

        _clock.Advance(TimeSpan.FromMinutes(2));
        await Pass(check);
        _payment.Asked.Should().HaveCount(1);
    }

    [Fact]
    public async Task OrdersFromBeforeTheLookback_AreNotAsked()
    {
        await Settled(TimeSpan.FromMinutes(45), payments: ("Succeeded", 100m));

        await Pass(Check());

        _payment.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task OrdersStillPending_AreNotJudged()
    {
        await Settled(Fresh, OrderStatus.Pending);

        await Pass(Check());

        _payment.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOrderIsCheckedOnce_NotAtEveryPass()
    {
        await Settled(Fresh, payments: ("Succeeded", 100m));
        var check = Check();

        await Pass(check);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Pass(check);

        _payment.Asked.Should().HaveCount(1);
    }

    [Fact]
    public async Task AnOrderThatChangesAgainAfterBeingChecked_IsCheckedAgain()
    {
        var id = await Settled(Fresh, payments: ("Succeeded", 100m));
        var check = Check();
        await Pass(check);

        // the customer cancels: Cancelled at a later time, refunded
        _clock.Advance(TimeSpan.FromMinutes(1));
        var order = await _db.Orders.SingleAsync(o => o.Id == id);
        order.Status = OrderStatus.Cancelled;
        order.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync();
        _payment.Holds[id] = [new PaymentRow(id, "Refunded", 100m)];
        _clock.Advance(TimeSpan.FromMinutes(3));
        await Pass(check);

        _payment.Asked.Should().Equal(id, id);
        _log.Messages(LogLevel.Error).Should().BeEmpty();
    }

    // ── a mismatch counts when two passes in a row find it ───────────────────

    [Fact]
    public async Task AMismatchSeenOnce_IsOnlyASuspicion_AndSeenTwice_IsAFinding()
    {
        var order = await Settled(Fresh, OrderStatus.Cancelled, payments: ("Succeeded", 100m));
        var check = Check();

        await Pass(check);
        (await Violations(ConsistencyRules.CancelledStillCharged)).Should().Be(0, "it may be a refund on its way");
        _log.Messages(LogLevel.Error).Should().BeEmpty();

        _clock.Advance(TimeSpan.FromMinutes(1));
        await Pass(check);
        (await Violations(ConsistencyRules.CancelledStillCharged)).Should().Be(1);
        _log.Messages(LogLevel.Error).Should().ContainSingle().Which
            .Should().Contain("CONSISTENCY VIOLATION cancelled_still_charged").And.Contain(order.ToString())
            .And.Contain("Succeeded 100");
    }

    [Fact]
    public async Task AFinding_StaysCounted_AndItsOrderIsLookedAtAgain_EvenAfterItLeavesTheLookback()
    {
        var order = await Settled(Fresh, payments: ("Failed", 100m)); // confirmed, but the payment failed
        var check = Check();
        await Pass(check);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Pass(check);

        _clock.Advance(TimeSpan.FromHours(2));
        await Pass(check);

        (await Violations(ConsistencyRules.ConfirmedNotPaid)).Should().Be(1, "nobody has put it right");
        _payment.Asked.Count(id => id == order).Should().Be(3);
        _log.Messages(LogLevel.Error).Should().ContainSingle("one line when it was found, not one per pass");
    }

    [Fact]
    public async Task AMismatchThatIsPutRight_StopsBeingCounted()
    {
        var order = await Settled(Fresh, OrderStatus.Cancelled, payments: ("Succeeded", 100m));
        var check = Check();
        await Pass(check);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Pass(check);

        _payment.Holds[order] = [new PaymentRow(order, "Refunded", 100m)]; // the refund went through
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Pass(check);

        (await Violations(ConsistencyRules.CancelledStillCharged)).Should().Be(0);
        _log.Messages(LogLevel.Information).Should().Contain(m => m.Contains("put right") && m.Contains(order.ToString()));
    }

    [Fact]
    public async Task AnOrderWhoseRefundIsUnderWay_IsNotJudged()
    {
        var order = await Settled(Fresh, payments: ("Refunded", 100m)); // still Confirmed, the saga is cancelling it
        _db.SagaStates.Add(new OrderSagaState
        {
            Id = Guid.NewGuid(), OrderId = order, Status = SagaStatus.RestockingInventory, UserId = "u", UserEmail = "u@test.com",
            TotalAmount = 100m, ItemsJson = "[]", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        var check = Check();

        await Pass(check);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Pass(check);

        _payment.Asked.Should().BeEmpty();
        (await Violations(ConsistencyRules.ConfirmedNotPaid)).Should().Be(0);
    }

    // ── when Payment cannot answer ───────────────────────────────────────────

    [Fact]
    public async Task WhenPaymentCannotBeAsked_ThePassFails_AndNothingIsConcludedFromIt()
    {
        var order = await Settled(Fresh, payments: ("Succeeded", 100m));
        var check = Check();
        _payment.Failure = new HttpRequestException("Payment is down");

        await check.Invoking(c => Pass(c)).Should().ThrowAsync<HttpRequestException>();
        _log.Messages(LogLevel.Error).Should().BeEmpty("an unanswered question is not a finding");

        _payment.Failure = null;
        await Pass(check);
        // the orders of the failed pass are asked about at the next one
        _payment.Asked.Should().Equal(order);
    }

    // ── large numbers of orders ──────────────────────────────────────────────

    [Fact]
    public async Task WhenMoreOrdersSettledThanOnePassTakes_TheRestFollowAtTheNextPasses()
    {
        var orders = new List<Guid>();
        for (var i = 0; i < 5; i++)
            orders.Add(await Settled(TimeSpan.FromMinutes(20 - i), payments: ("Succeeded", 100m)));
        var check = Check(new ConsistencyCheckOptions { MaxOrdersPerPass = 2 });

        await Pass(check);
        await Pass(check);
        await Pass(check);

        // oldest first, each once, none skipped
        _payment.Asked.Should().Equal(orders);
    }

    [Fact]
    public async Task AskingPayment_IsDoneInBatchesItAccepts()
    {
        for (var i = 0; i < 250; i++)
            await Settled(Fresh, payments: ("Succeeded", 100m));
        var payment = Substitute.For<IPaymentAuditClient>();
        payment.GetPaymentsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<PaymentRow>>(
                call.Arg<IReadOnlyCollection<Guid>>().Select(id => new PaymentRow(id, "Succeeded", 100m)).ToList()));

        await Check().CheckAsync(_db, payment, CancellationToken.None);

        await payment.Received(3).GetPaymentsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count <= PaymentAuditClient.MaxOrders), Arg.Any<CancellationToken>());
    }

    // ── the settings ─────────────────────────────────────────────────────────

    [Fact]
    public void TheDefaultSettings_AreAcceptable()
    {
        var options = new ConsistencyCheckOptions();

        options.IntervalIsValid.Should().BeTrue();
        options.SettleIsValid.Should().BeTrue();
        options.LookbackIsLongEnough.Should().BeTrue();
        options.MaxOrdersIsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(9)]
    [InlineData(120)] // the PollingLoopStalled alert would fire between two passes
    public void AnIntervalTheStalledLoopAlertWouldMisreadOrTooShort_IsRefused(int seconds) =>
        new ConsistencyCheckOptions { IntervalSeconds = seconds }.IntervalIsValid.Should().BeFalse();

    [Fact]
    public void ALookbackShorterThanTheTimeAnOrderNeedsToSettle_IsRefused() =>
        new ConsistencyCheckOptions { SettleSeconds = 600, LookbackMinutes = 10 }.LookbackIsLongEnough.Should().BeFalse();

    [Theory]
    [InlineData(0)]
    [InlineData(5001)]
    public void ANumberOfOrdersPerPassOutOfRange_IsRefused(int orders) =>
        new ConsistencyCheckOptions { MaxOrdersPerPass = orders }.MaxOrdersIsValid.Should().BeFalse();
}
