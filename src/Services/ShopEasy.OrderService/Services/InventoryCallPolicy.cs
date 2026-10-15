using Grpc.Core;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using ShopEasy.OrderService.Metrics;
using ShopEasy.Shared.Resilience;

namespace ShopEasy.OrderService.Services;

// Settings under "Resilience:Inventory".
public class InventoryCallOptions
{
    public const string SectionName = "Resilience:Inventory";

    // Deadline of one attempt. The call is cancelled (and Inventory is told, so it stops the
    // database query too) when it takes longer. A stock check that needs seconds is as good as down.
    public int TimeoutSeconds { get; set; } = 3;

    // Retries after a connection failure (Inventory restarting, a dropped connection). A slow answer
    // is never retried: that would only add load to a service that is already struggling.
    public int MaxRetries { get; set; } = 2;

    // How long the circuit stays open (calls fail at once, the stale cache answers) after
    // Inventory has been failing, before one trial call is let through.
    public int BreakDurationSeconds { get; set; } = 15;

    public void Validate()
    {
        if (TimeoutSeconds is < 1 or > 60)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(TimeoutSeconds)} must be between 1 and 60, not {TimeoutSeconds}.");
        if (MaxRetries is < 0 or > 5)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxRetries)} must be between 0 and 5, not {MaxRetries}.");
        if (BreakDurationSeconds is < 1 or > 600)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(BreakDurationSeconds)} must be between 1 and 600, not {BreakDurationSeconds}.");
    }
}

// How calls to the Inventory service are protected. One instance per process, because the
// circuit breaker and the retry budget have to see every call to be of any use:
//
//   retry (connection failures only, within the retry budget)
//     -> circuit breaker (opens when half of the recent calls failed)
//       -> timeout (one attempt, and the deadline sent to the server)
//
// Everything that goes wrong surfaces as InventoryUnavailableException; a caller that gave up
// (the customer closed the connection) gets an OperationCanceledException as usual.
public sealed class InventoryCallPolicy
{
    private const string Target = "inventory";

    private readonly ResiliencePipeline _pipeline;
    private readonly TimeSpan _timeout;

    public InventoryCallPolicy(
        IOptions<InventoryCallOptions> options,
        IOptions<RetryBudgetOptions> budgetOptions,
        ILogger<InventoryCallPolicy> logger)
    {
        var settings = options.Value;
        settings.Validate();

        _timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        var budget = new RetryBudget(budgetOptions.Value);
        var builder = new ResiliencePipelineBuilder();

        if (settings.MaxRetries > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = settings.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromSeconds(1),
                ShouldHandle = args =>
                {
                    // checked in this order: the budget is only spent on a retry that will happen
                    if (args.AttemptNumber >= settings.MaxRetries || !IsConnectionFailure(args.Outcome.Exception))
                        return ValueTask.FromResult(false);

                    var allowed = budget.TryAcquireRetry();
                    OrderMetrics.RecordRetry(Target, allowed ? "retried" : "budget_exhausted");
                    return ValueTask.FromResult(allowed);
                },
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception,
                        "Inventory call failed (attempt {Attempt}); retrying in {Delay} ms",
                        args.AttemptNumber + 1, (int)args.RetryDelay.TotalMilliseconds);
                    return default;
                }
            });
        }

        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(settings.BreakDurationSeconds),
            ShouldHandle = args => ValueTask.FromResult(IsServiceFailure(args.Outcome.Exception)),
            OnOpened = args =>
            {
                logger.LogError(
                    "Inventory circuit OPEN for {Break}s: the stale stock cache answers until it recovers",
                    (int)args.BreakDuration.TotalSeconds);
                OrderMetrics.RecordCircuitTransition(Target, "open");
                return default;
            },
            OnHalfOpened = _ =>
            {
                logger.LogInformation("Inventory circuit half-open: letting one trial call through");
                OrderMetrics.RecordCircuitTransition(Target, "half_open");
                return default;
            },
            OnClosed = _ =>
            {
                logger.LogInformation("Inventory circuit closed: calls are going through again");
                OrderMetrics.RecordCircuitTransition(Target, "closed");
                return default;
            }
        });

        builder.AddTimeout(_timeout);

        _pipeline = builder.Build();
    }

    // Runs one call. The delegate gets the deadline for this attempt (to give to gRPC, so the
    // server cancels the work as well) and a token that is cancelled at the deadline or when the
    // caller gives up.
    public async Task<T> ExecuteAsync<T>(
        Func<DateTime, CancellationToken, Task<T>> call, CancellationToken ct = default)
    {
        try
        {
            return await _pipeline.ExecuteAsync(async token =>
            {
                try
                {
                    return await call(DateTime.UtcNow + _timeout, token);
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && token.IsCancellationRequested)
                {
                    // gRPC reports a call cancelled by its token as an RpcException. It has to look
                    // like a cancellation to the pipeline, so that the timeout below is recognised as
                    // one (and counted by the circuit breaker) instead of being taken for a failure.
                    throw new OperationCanceledException(token);
                }
            }, ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // the caller went away; not an Inventory failure
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (RpcException ex)
        {
            throw new InventoryUnavailableException($"Inventory call failed: {ex.StatusCode}", ex);
        }
        catch (BrokenCircuitException ex)
        {
            throw new InventoryUnavailableException("Inventory circuit is open", ex);
        }
        catch (TimeoutRejectedException ex)
        {
            throw new InventoryUnavailableException(
                $"Inventory did not answer within {_timeout.TotalSeconds:0.#}s", ex);
        }
    }

    // Worth trying again at once: the request never got to the server
    internal static bool IsConnectionFailure(Exception? exception) =>
        exception is RpcException { StatusCode: StatusCode.Unavailable };

    // Counts against the circuit: the service is down, too slow or broken, not the caller's mistake
    internal static bool IsServiceFailure(Exception? exception) => exception switch
    {
        TimeoutRejectedException => true,
        RpcException rpc => rpc.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded
            or StatusCode.Internal or StatusCode.Unknown or StatusCode.ResourceExhausted
            or StatusCode.DataLoss or StatusCode.Aborted,
        _ => false
    };
}
