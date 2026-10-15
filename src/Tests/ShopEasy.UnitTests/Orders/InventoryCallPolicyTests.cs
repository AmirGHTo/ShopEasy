using System.Diagnostics;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopEasy.OrderService.Services;
using ShopEasy.Shared.Resilience;
using Xunit;

namespace ShopEasy.UnitTests.Orders;

// How a call to the Inventory service is protected: deadline, retry after connection failures
// (within a budget), circuit breaker. The calls are replaced by delegates that fail in the ways
// a real service fails.
public class InventoryCallPolicyTests
{
    private static InventoryCallPolicy Policy(
        int timeoutSeconds = 1, int retries = 2, int breakSeconds = 1, RetryBudgetOptions? budget = null) =>
        new(Options.Create(new InventoryCallOptions
            {
                TimeoutSeconds = timeoutSeconds, MaxRetries = retries, BreakDurationSeconds = breakSeconds
            }),
            Options.Create(budget ?? new RetryBudgetOptions { Ratio = 1, MinRetriesPerWindow = 100 }),
            NullLogger<InventoryCallPolicy>.Instance);

    private static RpcException Failure(StatusCode code) => new(new Status(code, code.ToString()));

    // A call that counts its attempts and fails with the given status for the first N of them
    private sealed class Flaky(StatusCode code, int failures)
    {
        private int _attempts;
        public int Attempts => _attempts;

        public Task<int> Call(DateTime deadline, CancellationToken token)
        {
            if (Interlocked.Increment(ref _attempts) <= failures) throw Failure(code);
            return Task.FromResult(42);
        }
    }

    [Fact]
    public async Task ASuccessfulCall_ReturnsItsResult_AndGetsADeadlineInTheFuture()
    {
        DateTime? seen = null;
        var before = DateTime.UtcNow;

        var result = await Policy(timeoutSeconds: 3).ExecuteAsync((deadline, _) =>
        {
            seen = deadline;
            return Task.FromResult("ok");
        });

        result.Should().Be("ok");
        seen.Should().BeCloseTo(before.AddSeconds(3), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AConnectionFailure_IsRetried_AndTheCallSucceedsWhenInventoryComesBack()
    {
        var flaky = new Flaky(StatusCode.Unavailable, failures: 2);

        var result = await Policy(retries: 2).ExecuteAsync(flaky.Call);

        result.Should().Be(42);
        flaky.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task WhenInventoryStaysDown_TheCallGivesUpAfterTheConfiguredRetries()
    {
        var flaky = new Flaky(StatusCode.Unavailable, failures: int.MaxValue);

        var act = () => Policy(retries: 2).ExecuteAsync(flaky.Call);

        (await act.Should().ThrowAsync<InventoryUnavailableException>())
            .Which.InnerException.Should().BeOfType<RpcException>();
        flaky.Attempts.Should().Be(3, "the first attempt and two retries");
    }

    [Theory]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.Internal)]
    [InlineData(StatusCode.InvalidArgument)]
    public async Task OnlyAConnectionFailureIsRetried_ASlowOrBrokenServerIsNot(StatusCode code)
    {
        var flaky = new Flaky(code, failures: int.MaxValue);

        var act = () => Policy(retries: 2).ExecuteAsync(flaky.Call);

        await act.Should().ThrowAsync<InventoryUnavailableException>();
        flaky.Attempts.Should().Be(1, "retrying would only add load to a struggling service");
    }

    [Fact]
    public async Task ACallThatNeverAnswers_IsCutOffAtTheTimeout_AndNotRetried()
    {
        var attempts = 0;
        var clock = Stopwatch.StartNew();

        var act = () => Policy(timeoutSeconds: 1, retries: 2).ExecuteAsync(async (_, token) =>
        {
            Interlocked.Increment(ref attempts);
            await Task.Delay(Timeout.Infinite, token); // honours the token, like gRPC does
            return 0;
        });

        await act.Should().ThrowAsync<InventoryUnavailableException>().WithMessage("*did not answer*");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "not the hours Task.Delay(Infinite) would take");
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task AHungGrpcCall_IsATimeout_EvenThoughGrpcReportsItAsCancelled()
    {
        // what a real gRPC call does when its token is cancelled: it throws RpcException(Cancelled)
        var policy = Policy(timeoutSeconds: 1, retries: 0, breakSeconds: 30);
        var attempts = 0;
        async Task<int> Hung(DateTime _, CancellationToken token)
        {
            Interlocked.Increment(ref attempts);
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { throw Failure(StatusCode.Cancelled); }
            return 0;
        }

        var first = await Record.ExceptionAsync(() => policy.ExecuteAsync(Hung));

        first.Should().BeOfType<InventoryUnavailableException>().Which.Message.Should().Contain("did not answer");

        // and it counts against the circuit like any other failure: after a few of them it opens
        for (var i = 0; i < 5; i++) await Record.ExceptionAsync(() => policy.ExecuteAsync(Hung));
        var attemptsWhenOpen = attempts;
        var open = await Record.ExceptionAsync(() => policy.ExecuteAsync(Hung));

        open.Should().BeOfType<InventoryUnavailableException>().Which.Message.Should().Contain("circuit");
        attempts.Should().Be(attemptsWhenOpen);
    }

    [Fact]
    public async Task WhenTheRetryBudgetIsUsedUp_ARetryIsNotMade()
    {
        var flaky = new Flaky(StatusCode.Unavailable, failures: int.MaxValue);
        var noBudget = new RetryBudgetOptions { Ratio = 0, MinRetriesPerWindow = 0 };

        var act = () => Policy(retries: 2, budget: noBudget).ExecuteAsync(flaky.Call);

        await act.Should().ThrowAsync<InventoryUnavailableException>();
        flaky.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task AfterRepeatedFailures_TheCircuitOpens_AndCallsFailWithoutReachingInventory()
    {
        var policy = Policy(retries: 0, breakSeconds: 30);
        var flaky = new Flaky(StatusCode.Unavailable, failures: int.MaxValue);

        for (var i = 0; i < 6; i++)
            await Record.ExceptionAsync(() => policy.ExecuteAsync(flaky.Call));
        var attemptsBefore = flaky.Attempts;

        var act = () => policy.ExecuteAsync(flaky.Call);

        await act.Should().ThrowAsync<InventoryUnavailableException>().WithMessage("*circuit*");
        flaky.Attempts.Should().Be(attemptsBefore, "the open circuit answered, Inventory was not called");
    }

    [Fact]
    public async Task TheCircuitClosesAgain_WhenATrialCallSucceedsAfterTheBreak()
    {
        var policy = Policy(retries: 0, breakSeconds: 1);
        var down = true;
        Task<int> Call(DateTime _, CancellationToken __) =>
            down ? throw Failure(StatusCode.Unavailable) : Task.FromResult(7);

        for (var i = 0; i < 6; i++) await Record.ExceptionAsync(() => policy.ExecuteAsync(Call));
        (await Record.ExceptionAsync(() => policy.ExecuteAsync(Call))).Should().BeOfType<InventoryUnavailableException>();

        down = false;
        await Task.Delay(TimeSpan.FromSeconds(1.3)); // the break is over: one trial call is let through

        (await policy.ExecuteAsync(Call)).Should().Be(7);
        (await policy.ExecuteAsync(Call)).Should().Be(7, "the trial succeeded, so the circuit is closed");
    }

    [Fact]
    public async Task ErrorsOfTheCaller_DoNotOpenTheCircuit()
    {
        var policy = Policy(retries: 0, breakSeconds: 30);
        var flaky = new Flaky(StatusCode.InvalidArgument, failures: int.MaxValue);

        for (var i = 0; i < 20; i++)
            await Record.ExceptionAsync(() => policy.ExecuteAsync(flaky.Call));

        flaky.Attempts.Should().Be(20, "every call still reached Inventory");
    }

    [Fact]
    public async Task ACallerThatGivesUp_GetsACancellation_NotAnInventoryFailure()
    {
        using var cts = new CancellationTokenSource();

        var act = () => Policy(timeoutSeconds: 10).ExecuteAsync(async (_, token) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(0, 2, 15)]
    [InlineData(61, 2, 15)]
    [InlineData(3, -1, 15)]
    [InlineData(3, 6, 15)]
    [InlineData(3, 2, 0)]
    public void NonsensicalSettings_AreRejected(int timeout, int retries, int breakSeconds)
    {
        var act = () => Policy(timeout, retries, breakSeconds);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Resilience:Inventory*");
    }
}
