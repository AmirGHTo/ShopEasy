using ShopEasy.OrderService.Services;

namespace ShopEasy.OrderService.Sagas;

// Settings under "SagaOrchestrator" (appsettings.json, or SagaOrchestrator__MaxAttempts and so on).
// When the orchestrator cannot send a saga's first command (the broker is down, say) it tries again
// after a growing pause, and gives up, cancelling the order, after MaxAttempts failures.
public class SagaOrchestratorOptions
{
    public const string SectionName = "SagaOrchestrator";

    // With the defaults the pauses are 3, 6, 12, 24, 48, 60 and 60 s: it gives up after about 3.5 minutes,
    // so a broker restart does not cost anyone an order
    public int MaxAttempts { get; set; } = 8;

    public int FirstRetryPauseSeconds { get; set; } = 3;

    // Below SagaTimeoutService.SagaTimeout, or the timeout service would cancel the order while it is
    // still being retried (every attempt counts as progress)
    public int MaxRetryPauseSeconds { get; set; } = 60;

    public bool MaxPauseIsShorterThanTheSagaTimeout => MaxRetryPauseSeconds < SagaTimeoutService.SagaTimeout.TotalSeconds;

    // The wait before the next attempt, once this many have failed
    public TimeSpan PauseAfter(int failedAttempts) => TimeSpan.FromSeconds(
        Math.Min(FirstRetryPauseSeconds * Math.Pow(2, failedAttempts - 1), MaxRetryPauseSeconds));
}
