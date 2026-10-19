namespace ShopEasy.OrderService.Services;

// An exception that leaves ExecuteAsync stops the whole Order host, so a polling loop runs each pass
// through here: a failure (typically the database restarting) is logged and the next pass tries again.
// Shutdown is not a failure: its cancellation still ends the loop.
internal static class GuardedPass
{
    public static async Task RunAsync(Func<Task> pass, ILogger logger, string service, CancellationToken ct)
    {
        try
        {
            await pass();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Service} pass failed; trying again at the next interval", service);
        }
    }
}
