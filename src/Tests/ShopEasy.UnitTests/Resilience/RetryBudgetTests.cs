using FluentAssertions;
using ShopEasy.Shared.Resilience;
using Xunit;

namespace ShopEasy.UnitTests.Resilience;

// How many retries a caller may add on top of its normal requests
public class RetryBudgetTests
{
    // A clock the test moves by hand
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static RetryBudget Budget(ManualClock clock, double ratio = 0.2, int min = 0, int window = 10) =>
        new(new RetryBudgetOptions { Ratio = ratio, MinRetriesPerWindow = min, WindowSeconds = window }, clock);

    [Fact]
    public void RetriesStayBelowTheRatioOfTheRequests()
    {
        var clock = new ManualClock();
        var budget = Budget(clock, ratio: 0.2);
        for (var i = 0; i < 50; i++) budget.RecordRequest();

        var allowed = Enumerable.Range(0, 30).Count(_ => budget.TryAcquireRetry());

        allowed.Should().Be(10, "20 % of 50 requests");
    }

    [Fact]
    public void WithoutAnyRequests_NoRetryIsAllowed_UnlessAMinimumIsConfigured()
    {
        var clock = new ManualClock();

        Budget(clock, min: 0).TryAcquireRetry().Should().BeFalse();
        Budget(clock, min: 3).TryAcquireRetry().Should().BeTrue();
    }

    [Fact]
    public void ThePerWindowMinimum_LetsAQuietCallerRetryAFewTimes()
    {
        var clock = new ManualClock();
        var budget = Budget(clock, ratio: 0.2, min: 5);
        budget.RecordRequest(); // one request: 20 % would allow nothing

        Enumerable.Range(0, 10).Count(_ => budget.TryAcquireRetry()).Should().Be(5);
    }

    [Fact]
    public void TheBudgetRecoversAsTheWindowSlides()
    {
        var clock = new ManualClock();
        var budget = Budget(clock, ratio: 0.5, min: 0, window: 10);
        for (var i = 0; i < 10; i++) budget.RecordRequest();
        Enumerable.Range(0, 10).Count(_ => budget.TryAcquireRetry()).Should().Be(5);
        budget.TryAcquireRetry().Should().BeFalse("the window is used up");

        clock.Advance(TimeSpan.FromSeconds(11)); // everything has left the window
        for (var i = 0; i < 10; i++) budget.RecordRequest();

        Enumerable.Range(0, 10).Count(_ => budget.TryAcquireRetry()).Should().Be(5);
    }

    [Fact]
    public void OldRequestsStopCounting_SoABurstDoesNotFundRetriesForever()
    {
        var clock = new ManualClock();
        var budget = Budget(clock, ratio: 1.0, window: 5);
        for (var i = 0; i < 100; i++) budget.RecordRequest();

        clock.Advance(TimeSpan.FromSeconds(6));

        budget.TryAcquireRetry().Should().BeFalse("those 100 requests are no longer in the window");
    }

    [Fact]
    public async Task ItIsSafeToUseFromManyThreads()
    {
        var budget = new RetryBudget(new RetryBudgetOptions { Ratio = 0.1, MinRetriesPerWindow = 0, WindowSeconds = 600 });
        var granted = 0;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                budget.RecordRequest();
                if (budget.TryAcquireRetry()) Interlocked.Increment(ref granted);
            }
        })));

        granted.Should().BeLessThanOrEqualTo(800, "10 % of the 8000 requests at most");
        granted.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(-0.1, 10, 10)]
    [InlineData(1.5, 10, 10)]
    [InlineData(0.2, -1, 10)]
    [InlineData(0.2, 10, 0)]
    [InlineData(0.2, 10, 601)]
    public void NonsensicalSettings_AreRejected(double ratio, int min, int window)
    {
        var act = () => new RetryBudget(new RetryBudgetOptions { Ratio = ratio, MinRetriesPerWindow = min, WindowSeconds = window });

        act.Should().Throw<InvalidOperationException>().WithMessage("*Resilience:RetryBudget*");
    }
}
