using FluentAssertions;
using Xunit;

namespace ShopEasy.UnitTests.Support;

// The limits that keep a stalled test bus from holding up the run (TestBus).
public class TestBusTests
{
    // If the helper were broken these tests must fail, not hang: they never wait for it unboundedly
    private static async Task<Task> FinishedWithin(Task call, TimeSpan limit) =>
        await Task.WhenAny(call, Task.Delay(limit));

    [Fact]
    public async Task AnOperationThatNeverFinishes_FailsWithAClearMessage_Instead_OfWaitingForever()
    {
        var never = new TaskCompletionSource().Task;

        var call = TestBus.Bounded(never, "start", TimeSpan.FromMilliseconds(200));

        (await FinishedWithin(call, TimeSpan.FromSeconds(10))).Should().BeSameAs(call, "the limit must end the wait");
        await call.Invoking(c => c).Should().ThrowAsync<TimeoutException>()
            .WithMessage("The test bus did not start within 0.2 s.");
    }

    [Fact]
    public async Task AnOperationThatBlocksItsThreadBeforeReturningATask_IsStillCutOff()
    {
        using var release = new ManualResetEventSlim();

        var call = TestBus.Bounded(() => { release.Wait(TimeSpan.FromSeconds(5)); return Task.CompletedTask; }, "stop", TimeSpan.FromMilliseconds(200));

        try
        {
            (await FinishedWithin(call, TimeSpan.FromSeconds(3))).Should().BeSameAs(call, "the limit must end the wait");
            await call.Invoking(c => c).Should().ThrowAsync<TimeoutException>()
                .WithMessage("The test bus did not stop within 0.2 s.");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task AnOperationThatFinishesInTime_PassesThrough()
    {
        await TestBus.Bounded(Task.Delay(20), "stop", TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AnOperationThatFails_KeepsItsOwnException()
    {
        var failing = Task.FromException(new InvalidOperationException("the broker refused"));

        await TestBus.Bounded(failing, "start", TimeSpan.FromSeconds(10))
            .Invoking(t => t).Should().ThrowAsync<InvalidOperationException>().WithMessage("the broker refused");
    }
}
