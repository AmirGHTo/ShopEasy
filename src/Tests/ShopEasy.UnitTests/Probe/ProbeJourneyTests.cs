using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ShopEasy.EndToEndTests.Support;
using ShopEasy.Probe;
using Xunit;

namespace ShopEasy.UnitTests.Probe;

// What the probe does in one run, against a shop that is a script: which steps it takes, which of them it
// blames, and that it never waits for something that cannot happen any more or leaves an order behind.
public class ProbeJourneyTests
{
    private static ProbeSettings Settings(int stepTimeoutSeconds = 5) => new()
    {
        Email = "probe@example.test",
        Password = "Probe-Pa55word-1",
        StepTimeoutSeconds = stepTimeoutSeconds,
        PollMilliseconds = 10, // the settings are not validated here: the tests must not wait half a second per poll
    };

    private static Task<Reply> Answer(HttpStatusCode status, object body) =>
        Reply.ReadAsync(new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        });

    // A shop, as the probe's customer meets it. By default everything works: the order is Pending for two
    // looks, then Confirmed and paid; after the cancellation it is still Confirmed for one look (the refund
    // is on the way), then Cancelled and refunded.
    private sealed class FakeShop : ICustomerSession
    {
        public List<string> Calls { get; } = [];
        public bool Disposed { get; private set; }
        private int _looks;
        private bool _cancelRequested;

        public Func<Task<Reply>> Product { get; set; } = () => Answer(HttpStatusCode.OK, new { price = 299.99m });
        public Func<Task<Reply>> Place { get; set; } = () => Answer(HttpStatusCode.Created, new { id = "order-1" });
        public Func<Task<Reply>> Cancel { get; set; } = () => Answer(HttpStatusCode.Accepted, new { });

        // (is the cancellation requested, how many looks since) -> status and payment status of the order
        public Func<bool, int, (string Status, string Payment)> Order { get; set; } = (cancelling, looks) =>
            !cancelling ? (looks < 2 ? ("Pending", "Pending") : ("Confirmed", "Succeeded"))
                        : (looks < 1 ? ("Confirmed", "Succeeded") : ("Cancelled", "Refunded"));

        public int Cancels => Calls.Count(c => c.StartsWith("DELETE", StringComparison.Ordinal));

        public Task<Reply> GetAsync(string path)
        {
            Calls.Add("GET " + path);
            if (path.StartsWith("/api/v1/products/", StringComparison.Ordinal))
                return Product();
            var (status, payment) = Order(_cancelRequested, _looks++);
            return Answer(HttpStatusCode.OK, new { orderId = "order-1", statusName = status, paymentStatus = payment });
        }

        public Task<Reply> PostAsync(string path, object body, string? idempotencyKey = null)
        {
            Calls.Add("POST " + path);
            return Place();
        }

        public Task<Reply> DeleteAsync(string path)
        {
            Calls.Add("DELETE " + path);
            _cancelRequested = true;
            _looks = 0;
            return Cancel();
        }

        public void Dispose() => Disposed = true;
    }

    private static async Task<(IReadOnlyList<StepResult> Steps, FakeShop Shop)> RunAsync(
        Action<FakeShop>? script = null, ProbeSettings? settings = null)
    {
        var shop = new FakeShop();
        script?.Invoke(shop);
        settings ??= Settings();
        var steps = await new Journey(settings, () => Task.FromResult<ICustomerSession>(shop)).RunAsync(CancellationToken.None);
        return (steps, shop);
    }

    // ── a shop that works ─────────────────────────────────────────────────────

    [Fact]
    public async Task AWorkingShop_PassesEveryStepInOrder_AndTheOrderIsGivenBack()
    {
        var (steps, shop) = await RunAsync();

        steps.Select(s => s.Step).Should().Equal(Journey.Steps);
        steps.Should().OnlyContain(s => s.Ok && s.Error == null);
        shop.Cancels.Should().Be(1, "the order is cancelled once, by the last step, and not again");
        shop.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task TheOrderIsConfirmed_EvenWhenTheStatusIsAskedForSeveralTimes()
    {
        var (steps, shop) = await RunAsync(s =>
            s.Order = (cancelling, looks) => !cancelling ? (looks < 6 ? ("Pending", "Pending") : ("Confirmed", "Succeeded")) : ("Cancelled", "Refunded"));

        steps.Should().OnlyContain(s => s.Ok);
        shop.Calls.Count(c => c == "GET /api/orders/order-1").Should().BeGreaterThan(6);
    }

    [Fact]
    public async Task ACancelledOrderThatIsStillConfirmedWhileTheRefundIsOnItsWay_IsNotAFailure()
    {
        // found by running the probe against a real stack: the status stays Confirmed until the refund is done
        var (steps, _) = await RunAsync(s =>
            s.Order = (cancelling, looks) => !cancelling ? ("Confirmed", "Succeeded") : (looks < 4 ? ("Confirmed", "Succeeded") : ("Cancelled", "Refunded")));

        steps.Should().OnlyContain(s => s.Ok);
    }

    // ── a step that fails ends the run, and is the one that is blamed ─────────

    [Fact]
    public async Task WhenSigningInFails_ThatIsTheOnlyStepReported()
    {
        var shop = new FakeShop();
        var journey = new Journey(Settings(), () => throw new SignInRefusedException("Identity said no"));

        var steps = await journey.RunAsync(CancellationToken.None);

        steps.Should().ContainSingle().Which.Should().Match<StepResult>(s => s.Step == Journey.SignIn && !s.Ok && s.Error!.Contains("Identity said no"));
        shop.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenTheProductIsNotThere_TheCatalogIsBlamed_AndNothingIsOrdered()
    {
        var (steps, shop) = await RunAsync(s => s.Product = () => Answer(HttpStatusCode.NotFound, new { }));

        steps.Select(s => (s.Step, s.Ok)).Should().Equal((Journey.SignIn, true), (Journey.Catalog, false));
        shop.Calls.Should().NotContain(c => c.StartsWith("POST"));
    }

    [Fact]
    public async Task WhenTheOrderIsRefused_PlaceOrderIsBlamed_WithWhatTheShopSaid()
    {
        var (steps, _) = await RunAsync(s => s.Place = () => Answer(HttpStatusCode.TooManyRequests, new { error = "Too many requests" }));

        steps.Last().Should().Match<StepResult>(s => s.Step == Journey.PlaceOrder && !s.Ok && s.Error!.Contains("429") && s.Error.Contains("Too many requests"));
        steps.Should().HaveCount(3);
    }

    [Fact]
    public async Task AnOrderThatIsNeverConfirmed_BlamesTheConfirmation_AfterTheTimeout_AndIsCancelled()
    {
        var (steps, shop) = await RunAsync(
            s => s.Order = (cancelling, looks) => cancelling ? ("Cancelled", "Refunded") : ("Pending", "Pending"),
            Settings(stepTimeoutSeconds: 1));

        steps.Last().Should().Match<StepResult>(s => s.Step == Journey.Confirmation && !s.Ok && s.Error!.Contains("not Confirmed after 1 s"));
        steps.Should().HaveCount(4, "the cancellation is not tried as a step of its own");
        shop.Cancels.Should().Be(1, "the order that is still in flight is cancelled, so a long outage does not leave one order a minute behind");
    }

    [Fact]
    public async Task AnOrderThatIsCancelledByTheShop_FailsTheConfirmationAtOnce_NotAfterTheTimeout()
    {
        var clock = Stopwatch.StartNew();

        var (steps, _) = await RunAsync(s => s.Order = (_, _) => ("Cancelled", "Refunded"), Settings(stepTimeoutSeconds: 30));

        steps.Last().Should().Match<StepResult>(s => s.Step == Journey.Confirmation && !s.Ok && s.Error!.Contains("Cancelled"));
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "a status from which the order can never be confirmed is not waited out");
    }

    [Fact]
    public async Task AConfirmedOrderThatWasNotPaid_BlamesTheConfirmation()
    {
        var (steps, _) = await RunAsync(s => s.Order = (cancelling, _) => cancelling ? ("Cancelled", "Refunded") : ("Confirmed", "Pending"));

        steps.Last().Should().Match<StepResult>(s => s.Step == Journey.Confirmation && !s.Ok && s.Error!.Contains("not paid"));
    }

    [Fact]
    public async Task AnOrderThatFails_BlamesTheConfirmation_AtOnce()
    {
        var (steps, _) = await RunAsync(s => s.Order = (_, _) => ("Failed", "Pending"), Settings(stepTimeoutSeconds: 30));

        steps.Last().Should().Match<StepResult>(s => s.Step == Journey.Confirmation && !s.Ok && s.Error!.Contains("Failed"));
    }

    [Fact]
    public async Task ACancellationThatIsRefused_BlamesTheCancel_AndTriesAgainToLeaveNothingBehind()
    {
        var (steps, shop) = await RunAsync(s => s.Cancel = () => Answer(HttpStatusCode.InternalServerError, new { }));

        steps.Last().Should().Match<StepResult>(s => s.Step == Journey.Cancel && !s.Ok);
        steps.Should().HaveCount(5);
        shop.Cancels.Should().Be(2, "once as the step, once to clean up");
    }

    [Fact]
    public async Task ACancelledOrderWhoseMoneyWasNotGivenBack_BlamesTheCancel()
    {
        var (steps, _) = await RunAsync(s =>
            s.Order = (cancelling, looks) => cancelling ? ("Cancelled", "Succeeded") : (looks < 1 ? ("Pending", "Pending") : ("Confirmed", "Succeeded")));

        steps.Last().Should().Match<StepResult>(s => s.Step == Journey.Cancel && !s.Ok && s.Error!.Contains("money was not given back"));
    }

    [Fact]
    public async Task ARunThatIsStopped_IsNotAFailureOfTheShop()
    {
        using var stop = new CancellationTokenSource();
        var shop = new FakeShop { Order = (_, _) => ("Pending", "Pending") };
        var journey = new Journey(Settings(stepTimeoutSeconds: 30), () => Task.FromResult<ICustomerSession>(shop));
        stop.CancelAfter(TimeSpan.FromMilliseconds(200));

        var run = () => journey.RunAsync(stop.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        shop.Disposed.Should().BeTrue();
    }

    // ── what the probe asks of the shop, so that it is never the one to use up the limit ──

    [Fact]
    public void AMinuteOfWaiting_CostsFewRequests_WhateverTheShopDoes()
    {
        // the gateway lets a signed-in user make 100 requests a minute (GatewayRateLimitSettings)
        foreach (var pollMilliseconds in new[] { ProbeSettings.MinPollMilliseconds, 1000, 2000 })
        {
            var settings = new ProbeSettings { PollMilliseconds = pollMilliseconds };
            var waited = TimeSpan.Zero;
            var requests = 0;
            while (waited < TimeSpan.FromMinutes(1))
            {
                requests++;
                waited += settings.PollAfter(waited);
            }

            requests.Should().BeLessThanOrEqualTo(45, $"asking every {pollMilliseconds} ms for 15 s and every 3 s after that");
        }
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(14, 1000)]
    [InlineData(15, 3000)]
    [InlineData(59, 3000)]
    public void TheProbeAsksQuicklyAtFirst_AndSlowlyWhenTheShopIsTakingLong(int secondsWaited, int expectedMilliseconds)
        => new ProbeSettings { PollMilliseconds = 1000 }.PollAfter(TimeSpan.FromSeconds(secondsWaited))
            .Should().Be(TimeSpan.FromMilliseconds(expectedMilliseconds));

    [Fact]
    public void AnInterval_LongerThanTheSlowPoll_IsNeverShortened()
        => new ProbeSettings { PollMilliseconds = 5000 }.PollAfter(TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(5));
}
