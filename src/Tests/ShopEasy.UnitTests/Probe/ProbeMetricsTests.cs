using FluentAssertions;
using Microsoft.Extensions.Logging;
using ShopEasy.EndToEndTests.Support;
using ShopEasy.Probe;
using ShopEasy.UnitTests.Support;
using Xunit;

namespace ShopEasy.UnitTests.Probe;

// What the probe reports to Prometheus after a run, and what SyntheticOrderFailing and SyntheticProbeStale
// are written against. The metrics are static and shared, so these tests run alone (Metrics collection).
[Collection(MetricsCollection.Name)]
public class ProbeMetricsTests
{
    private static StepResult Ok(string step, double seconds = 1) => new(step, true, seconds);
    private static StepResult Failed(string step, double seconds = 2) => new(step, false, seconds, "it did not work");

    private static IReadOnlyList<StepResult> CompleteRun() => Journey.Steps.Select(step => Ok(step, 0.5)).ToList();

    private static string Success(string step) => $"shopeasy_probe_success{{step=\"{step}\"}}";
    private static string Duration(string step) => $"shopeasy_probe_duration_seconds{{step=\"{step}\"}}";

    private static readonly DateTimeOffset Noon = new(2030, 1, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ACompleteRun_ReportsEveryStepAsWorking_WithItsDuration()
    {
        ProbeMetrics.Record(CompleteRun(), Noon);

        foreach (var step in Journey.Steps)
        {
            (await MetricValues.ReadAsync(Success(step))).Should().Be(1, step);
            (await MetricValues.ReadAsync(Duration(step))).Should().Be(0.5, step);
        }
        (await MetricValues.ReadAsync("shopeasy_probe_consecutive_failures")).Should().Be(0);
        (await MetricValues.ReadAsync("shopeasy_probe_last_success_timestamp_seconds")).Should().Be(Noon.ToUnixTimeSeconds());
        (await MetricValues.ReadAsync("shopeasy_probe_last_run_timestamp_seconds")).Should().Be(Noon.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task AFailedStep_IsReportedAsZero_AndTheStepsAfterItAreNotReportedAtAll()
    {
        ProbeMetrics.Record(CompleteRun(), Noon);

        ProbeMetrics.Record([Ok(Journey.SignIn), Ok(Journey.Catalog), Ok(Journey.PlaceOrder), Failed(Journey.Confirmation, 60)], Noon.AddMinutes(1));

        (await MetricValues.ReadAsync(Success(Journey.PlaceOrder))).Should().Be(1);
        (await MetricValues.ReadAsync(Success(Journey.Confirmation))).Should().Be(0);
        (await MetricValues.ReadAsync(Duration(Journey.Confirmation))).Should().Be(60);
        (await MetricValues.ExistsAsync(Success(Journey.Cancel)))
            .Should().BeFalse("a step that was not reached has no value: the success of the run before must not look like the success of this one");
        (await MetricValues.ExistsAsync(Duration(Journey.Cancel))).Should().BeFalse();
    }

    [Fact]
    public async Task FailedRunsAreCounted_InARow_AndOneCompleteRunStartsOver()
    {
        ProbeMetrics.Record(CompleteRun(), Noon);
        var lastSuccess = await MetricValues.ReadAsync("shopeasy_probe_last_success_timestamp_seconds");

        ProbeMetrics.Record([Failed(Journey.SignIn)], Noon.AddMinutes(1));
        ProbeMetrics.Record([Ok(Journey.SignIn), Failed(Journey.Catalog)], Noon.AddMinutes(2));
        ProbeMetrics.Record([Ok(Journey.SignIn), Failed(Journey.Catalog)], Noon.AddMinutes(3));

        (await MetricValues.ReadAsync("shopeasy_probe_consecutive_failures")).Should().Be(3);
        (await MetricValues.ReadAsync("shopeasy_probe_last_success_timestamp_seconds"))
            .Should().Be(lastSuccess, "only a complete run moves the last success");
        (await MetricValues.ReadAsync("shopeasy_probe_last_run_timestamp_seconds"))
            .Should().Be(Noon.AddMinutes(3).ToUnixTimeSeconds(), "a failed run is still a run: the probe is alive");

        ProbeMetrics.Record(CompleteRun(), Noon.AddMinutes(4));

        (await MetricValues.ReadAsync("shopeasy_probe_consecutive_failures")).Should().Be(0);
        (await MetricValues.ReadAsync("shopeasy_probe_last_success_timestamp_seconds")).Should().Be(Noon.AddMinutes(4).ToUnixTimeSeconds());
    }

    [Fact]
    public async Task ARunThatStoppedShort_WithoutAFailedStep_IsStillAFailedRun()
    {
        ProbeMetrics.Record(CompleteRun(), Noon);

        ProbeMetrics.Record([Ok(Journey.SignIn), Ok(Journey.Catalog)], Noon.AddMinutes(1));

        (await MetricValues.ReadAsync("shopeasy_probe_consecutive_failures")).Should().Be(1);
    }

    [Fact]
    public async Task RunsAreCounted_ByHowTheyEnded()
    {
        var success = await MetricValues.ReadAsync("shopeasy_probe_runs_total{result=\"success\"}");
        var failure = await MetricValues.ReadAsync("shopeasy_probe_runs_total{result=\"failure\"}");

        ProbeMetrics.Record(CompleteRun(), Noon);
        ProbeMetrics.Record([Failed(Journey.SignIn)], Noon.AddMinutes(1));
        ProbeMetrics.Record([Failed(Journey.SignIn)], Noon.AddMinutes(2));

        (await MetricValues.ReadAsync("shopeasy_probe_runs_total{result=\"success\"}")).Should().Be(success + 1);
        (await MetricValues.ReadAsync("shopeasy_probe_runs_total{result=\"failure\"}")).Should().Be(failure + 2);
    }

    // ── the service around the journey ────────────────────────────────────────

    private static ProbeService ServiceWhoseSignInFails(ListLogger<ProbeService> log) =>
        new(new Journey(new ProbeSettings { Email = "p@example.test", Password = "x" },
                () => throw new SignInRefusedException("Identity said no")),
            new ProbeSettings(), log);

    [Fact]
    public async Task AFailedRun_IsMeasured_AndLogged_WithTheStepThatFailedAndWhy()
    {
        ProbeMetrics.Record(CompleteRun(), Noon);
        var log = new ListLogger<ProbeService>();

        await ServiceWhoseSignInFails(log).RunOnceAsync(CancellationToken.None);

        (await MetricValues.ReadAsync(Success(Journey.SignIn))).Should().Be(0);
        (await MetricValues.ReadAsync("shopeasy_probe_consecutive_failures")).Should().Be(1);
        log.Messages(LogLevel.Warning).Should().ContainSingle()
            .Which.Should().Contain("sign_in").And.Contain("Identity said no");
    }

    [Fact]
    public async Task AStoppingProbe_ReportsNothing()
    {
        ProbeMetrics.Record(CompleteRun(), Noon);
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();
        var log = new ListLogger<ProbeService>();

        await ServiceWhoseSignInFails(log).RunOnceAsync(stopped.Token);

        (await MetricValues.ReadAsync("shopeasy_probe_consecutive_failures")).Should().Be(0, "the container is stopping: that is not a failure of the shop");
        log.Entries.Should().BeEmpty();
    }
}
