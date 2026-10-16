using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ShopEasy.UnitTests.Support;

// A MassTransit test bus that cannot hold up the whole test run. If starting or stopping the bus
// ever stalls, its test would otherwise block until the runner's 5-minute hang detector aborts the
// entire run, naming a test that "may or may not be the source". With these limits that one test
// fails after 30 s and says what it was waiting for; the rest of the run carries on.
public static class TestBus
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    public static Task StartWithinLimitAsync(this ITestHarness harness) => Bounded(harness.Start(), "start", Limit);

    // For `await using var _ = provider.StopWithinLimit();` in place of `await using var _ = provider;`
    public static IAsyncDisposable StopWithinLimit(this ServiceProvider provider) => new Stopper(provider);

    public static async Task Bounded(Task operation, string what, TimeSpan limit)
    {
        try
        {
            await operation.WaitAsync(limit);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The test bus did not {what} within {limit.TotalSeconds:0.##} s.");
        }
    }

    private sealed class Stopper(ServiceProvider provider) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Bounded(provider.DisposeAsync().AsTask(), "stop", Limit);
    }
}
