using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ShopEasy.OrderService.Services;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Observability;

// A polling loop that fails or hangs is visible without reading the logs: the failed passes are counted,
// and the time of the last success is what the PollingLoopStalled alert measures against.
[Collection(MetricsCollection.Name)]
public class BackgroundLoopMetricsTests
{
    private const string FailedPasses = "shopeasy_background_loop_failed_passes_total{loop=\"metrics_test_loop\"}";
    private const string LastSuccess = "shopeasy_background_loop_last_success_timestamp_seconds{loop=\"metrics_test_loop\"}";

    private static GuardedPass Guard() => new(NullLogger.Instance, "Metrics test loop");

    private static Task Fail(GuardedPass guard) =>
        guard.RunAsync(_ => throw new InvalidOperationException("database is restarting"), CancellationToken.None);

    private static Task Succeed(GuardedPass guard) => guard.RunAsync(_ => Task.CompletedTask, CancellationToken.None);

    [Fact]
    public async Task EveryFailedPassIsCounted()
    {
        var guard = Guard();
        var before = await MetricValues.ReadAsync(FailedPasses);

        await Fail(guard);
        await Fail(guard);
        await Succeed(guard);

        (await MetricValues.ReadAsync(FailedPasses)).Should().Be(before + 2);
    }

    [Fact]
    public async Task TheLastSuccessIsSetWhenTheLoopIsCreated_AndOnlyASuccessMovesItOn()
    {
        var started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var guard = Guard();
        var created = await MetricValues.ReadAsync(LastSuccess);
        created.Should().BeInRange(started - 1, started + 5);   // a loop that never completes a pass still ages

        await Task.Delay(1500);
        await Fail(guard);
        (await MetricValues.ReadAsync(LastSuccess)).Should().Be(created);

        await Succeed(guard);
        (await MetricValues.ReadAsync(LastSuccess)).Should().BeGreaterThan(created + 1);
    }
}
