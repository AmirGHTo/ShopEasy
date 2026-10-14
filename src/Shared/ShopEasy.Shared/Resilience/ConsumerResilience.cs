using MassTransit;
using Microsoft.Extensions.Configuration;

namespace ShopEasy.Shared.Resilience;

// How a message consumer reacts when processing fails. There are two kinds of failure:
//
//  * The infrastructure is unavailable for a while (PostgreSQL restarting or failing over, a
//    network blip, a timeout). Retrying later will work, and giving up would only move the message
//    to an error queue for a human to requeue. So these are retried patiently, with growing
//    pauses, for several minutes. The message stays unacknowledged meanwhile; the consumer
//    slots it occupies are the back-pressure (nothing new is taken from the queue), and a pod
//    that dies meanwhile simply has the message redelivered.
//
//  * Anything else (a bug, a rule that will never be satisfied, a unique-key conflict between two
//    copies of a message). A few quick retries cover races, then the message goes to the error
//    queue where the dead-letter monitor shows it.
//
// Settings live under "Resilience:Consumers".
public class ConsumerResilienceOptions
{
    public const string SectionName = "Resilience:Consumers";

    // Patient retries for transient infrastructure faults
    public int TransientRetryLimit { get; set; } = 12;
    public double TransientMinIntervalSeconds { get; set; } = 1;
    public double TransientMaxIntervalSeconds { get; set; } = 60;

    // Spread of the pauses (+/- this fraction, chosen once per process) so that the replicas of a
    // service do not all hit a database that has just come back at the same instant
    public double JitterRatio { get; set; } = 0.2;

    // Quick retries for every other failure
    public int RetryLimit { get; set; } = 3;
    public double MinIntervalSeconds { get; set; } = 1;
    public double MaxIntervalSeconds { get; set; } = 10;
    public double IntervalDeltaSeconds { get; set; } = 2;

    public static ConsumerResilienceOptions From(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<ConsumerResilienceOptions>() ?? new ConsumerResilienceOptions();

    // 1 s, 2 s, 4 s ... doubling up to the maximum, then the maximum for the remaining attempts
    public TimeSpan[] TransientIntervals(Random? random = null)
    {
        if (TransientRetryLimit < 0 || TransientMinIntervalSeconds <= 0 ||
            TransientMaxIntervalSeconds < TransientMinIntervalSeconds || JitterRatio is < 0 or >= 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}: the transient retry settings are invalid (limit >= 0, 0 < min <= max, 0 <= jitter < 1).");
        }

        random ??= Random.Shared;
        var intervals = new TimeSpan[TransientRetryLimit];
        var seconds = TransientMinIntervalSeconds;
        for (var i = 0; i < intervals.Length; i++)
        {
            var jitter = 1 + (random.NextDouble() * 2 - 1) * JitterRatio;
            intervals[i] = TimeSpan.FromSeconds(seconds * jitter);
            seconds = Math.Min(seconds * 2, TransientMaxIntervalSeconds);
        }

        return intervals;
    }
}

public static class ConsumerResilience
{
    // Call on the bus (before the receive endpoints are declared), in place of UseMessageRetry.
    public static void UseConsumerResilience(
        this IBusFactoryConfigurator configurator, ConsumerResilienceOptions options)
    {
        var transientIntervals = options.TransientIntervals();

        // Outer filter: the patient one. It only handles transient faults...
        if (transientIntervals.Length > 0)
        {
            configurator.UseMessageRetry(r =>
            {
                r.Handle<Exception>(TransientFaults.IsTransient);
                r.Intervals(transientIntervals);
            });
        }

        // ...inner filter: the quick one. It lets transient faults pass straight up to the filter
        // above, and never retries what cannot succeed.
        configurator.UseMessageRetry(r =>
        {
            r.Exponential(
                retryLimit: options.RetryLimit,
                minInterval: TimeSpan.FromSeconds(options.MinIntervalSeconds),
                maxInterval: TimeSpan.FromSeconds(options.MaxIntervalSeconds),
                intervalDelta: TimeSpan.FromSeconds(options.IntervalDeltaSeconds));

            r.Ignore<ArgumentNullException>();
            r.Ignore<InvalidOperationException>();
            if (transientIntervals.Length > 0)
                r.Ignore<Exception>(TransientFaults.IsTransient);
        });
    }
}
