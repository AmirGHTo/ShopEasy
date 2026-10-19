using Microsoft.Extensions.Options;

namespace ShopEasy.OrderService.Services;

// An exception that leaves ExecuteAsync stops the whole Order host, so each polling loop runs its
// passes through one of these. A failure (typically the database restarting) is logged and the next
// pass tries again; a pass that hangs is cancelled after the pass timeout and counts as a failure.
// Shutdown is not a failure: its cancellation still ends the loop.
//
// An outage is logged as a few lines, not one per pass: the first failure with its exception, a line
// each time the number of failed passes in a row doubles, and a line when a pass succeeds again.
internal sealed class GuardedPass(ILogger logger, string service, IOptions<BackgroundLoopOptions>? options = null)
{
    private readonly TimeSpan _passTimeout =
        TimeSpan.FromSeconds((options?.Value ?? new BackgroundLoopOptions()).PassTimeoutSeconds);
    private int _failedInARow;
    private DateTime _failingSince;

    public async Task RunAsync(Func<CancellationToken, Task> pass, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_passTimeout);
        try
        {
            await pass(limit.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Failed(limit.IsCancellationRequested
                ? new TimeoutException($"The pass did not finish within {_passTimeout.TotalSeconds:0} s and was cancelled", ex)
                : ex);
            return;
        }

        if (_failedInARow == 0) return;
        logger.LogInformation("{Service} recovered after {Failures} failed pass(es) over {Seconds:0} s",
            service, _failedInARow, (DateTime.UtcNow - _failingSince).TotalSeconds);
        _failedInARow = 0;
    }

    private void Failed(Exception error)
    {
        var failures = ++_failedInARow;
        if (failures == 1)
        {
            _failingSince = DateTime.UtcNow;
            logger.LogError(error, "{Service} pass failed; trying again at the next interval", service);
        }
        else if ((failures & (failures - 1)) == 0)
        {
            logger.LogWarning("{Service} is still failing: {Failures} passes in a row since {Since:HH:mm:ss} UTC. Last error: {Error}",
                service, failures, _failingSince, error.Message);
        }
    }
}
