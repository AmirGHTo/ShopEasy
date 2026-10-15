using ShopEasy.Shared.Resilience;

namespace ShopEasy.UnitTests.Support;

public static class TestResilience
{
    // The production retry policy with the pauses shortened to milliseconds, so a test that rides
    // out a dozen failed attempts takes a fraction of a second instead of minutes. Which failures
    // are retried, how many times and when the message is given up on are exactly as in production.
    public static ConsumerResilienceOptions FastConsumers(int transientRetryLimit = 12) => new()
    {
        TransientRetryLimit = transientRetryLimit,
        TransientMinIntervalSeconds = 0.01,
        TransientMaxIntervalSeconds = 0.05,
        JitterRatio = 0,
        RetryLimit = 3,
        MinIntervalSeconds = 0.01,
        MaxIntervalSeconds = 0.02,
        IntervalDeltaSeconds = 0.01
    };
}
