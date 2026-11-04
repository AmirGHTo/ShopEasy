using System.Diagnostics;

namespace ShopEasy.Probe;

// Runs the journey on a schedule, for as long as the container lives. A run that fails is a result, not an
// error: it is logged, measured, and the next one starts on time.
public sealed class ProbeService(Journey journey, ProbeSettings settings, ILogger<ProbeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ProbeMetrics.Start();
        logger.LogInformation(
            "Synthetic probe started: {Email} orders {Product} through the gateway every {Interval} s",
            settings.Email, settings.ProductId, settings.Interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            await RunOnceAsync(stoppingToken);

            var rest = settings.Interval - Stopwatch.GetElapsedTime(started);
            if (rest > TimeSpan.Zero)
                await Task.Delay(rest, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<StepResult> results;
        try
        {
            results = await journey.RunAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // the container is stopping: that is not a failure of the shop
        }
        catch (Exception error)
        {
            // the journey reports its own steps; this is a bug of the probe, which must not stop it
            logger.LogError(error, "The journey itself failed");
            results = [new StepResult(Journey.SignIn, false, 0, error.Message)];
        }

        ProbeMetrics.Record(results, DateTimeOffset.UtcNow);

        var failed = results.FirstOrDefault(r => !r.Ok);
        var steps = string.Join(", ", results.Select(r => $"{r.Step} {(r.Ok ? "ok" : "FAILED")} {r.Seconds:0.0} s"));
        if (failed is null && results.Count == Journey.Steps.Length)
            logger.LogInformation("Probe run ok: {Steps}", steps);
        else
            logger.LogWarning("Probe run failed at {Step}: {Error} ({Steps})", failed?.Step ?? "?", failed?.Error ?? "incomplete", steps);
    }
}
