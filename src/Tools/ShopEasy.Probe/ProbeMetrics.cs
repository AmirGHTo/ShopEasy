using Prometheus;

namespace ShopEasy.Probe;

// What the last run of the probe found, for Prometheus (alerts: SyntheticOrderFailing, SyntheticProbeStale)
public static class ProbeMetrics
{
    private static readonly Gauge Success = Metrics.CreateGauge(
        "shopeasy_probe_success",
        "1 when the step of the probe's last run worked, 0 when it failed. A step that was not reached (an earlier one failed) has no value",
        new GaugeConfiguration { LabelNames = ["step"] });

    private static readonly Gauge Duration = Metrics.CreateGauge(
        "shopeasy_probe_duration_seconds",
        "How long the step of the probe's last run took (until it failed, for a failed step)",
        new GaugeConfiguration { LabelNames = ["step"] });

    private static readonly Gauge ConsecutiveFailures = Metrics.CreateGauge(
        "shopeasy_probe_consecutive_failures",
        "Runs of the probe in a row that did not complete the whole journey (0 after one that did)");

    private static readonly Gauge LastSuccess = Metrics.CreateGauge(
        "shopeasy_probe_last_success_timestamp_seconds",
        "When the probe last completed the whole journey (Unix time)");

    private static readonly Gauge LastRun = Metrics.CreateGauge(
        "shopeasy_probe_last_run_timestamp_seconds",
        "When the probe last finished a run, however it ended (Unix time): time() minus this is how long it has been silent");

    private static readonly Counter Runs = Metrics.CreateCounter(
        "shopeasy_probe_runs_total",
        "Runs of the probe, by how they ended",
        new CounterConfiguration { LabelNames = ["result"] });

    // The journey has not run yet: no failure is known
    public static void Start()
    {
        ConsecutiveFailures.Set(0);
        Runs.WithLabels("success");
        Runs.WithLabels("failure");
    }

    public static void Record(IReadOnlyList<StepResult> results, DateTimeOffset now)
    {
        var complete = results.Count == Journey.Steps.Length && results.All(r => r.Ok);

        foreach (var step in Journey.Steps)
        {
            if (results.FirstOrDefault(r => r.Step == step) is { } result)
            {
                Success.WithLabels(step).Set(result.Ok ? 1 : 0);
                Duration.WithLabels(step).Set(result.Seconds);
            }
            else
            {
                // not reached: say nothing rather than repeat what an earlier run found
                Success.RemoveLabelled(step);
                Duration.RemoveLabelled(step);
            }
        }

        Runs.WithLabels(complete ? "success" : "failure").Inc();
        var timestamp = now.ToUnixTimeMilliseconds() / 1000.0;
        LastRun.Set(timestamp);
        if (complete)
        {
            LastSuccess.Set(timestamp);
            ConsecutiveFailures.Set(0);
        }
        else
        {
            ConsecutiveFailures.Inc();
        }
    }
}
