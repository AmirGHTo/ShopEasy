using Microsoft.Extensions.Http.Resilience;
using Polly.Retry;
using ShopEasy.OrderService.Metrics;
using ShopEasy.Shared.Resilience;

namespace ShopEasy.OrderService.Services;

// Counts every original request to a service, so the retry budget knows how many retries the
// traffic can afford. Registered as the OUTERMOST handler of the client: the retries the
// resilience handler makes are then not counted as requests.
public sealed class RetryBudgetHandler(RetryBudget budget) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        budget.RecordRequest();
        return base.SendAsync(request, cancellationToken);
    }
}

public static class HttpRetryBudget
{
    // Which failures the standard resilience handler retries: the same transient ones it retries by
    // default (5xx, 408, 429, network errors, timeouts), but only while the budget allows it, so a
    // service that has started to fail is not also hit by everybody's retries.
    public static Func<RetryPredicateArguments<HttpResponseMessage>, ValueTask<bool>> ShouldRetry(
        RetryBudget budget, string target, int maxRetries) =>
        args =>
        {
            if (args.AttemptNumber >= maxRetries || !HttpClientResiliencePredicates.IsTransient(args.Outcome))
                return ValueTask.FromResult(false);

            var allowed = budget.TryAcquireRetry();
            OrderMetrics.RecordRetry(target, allowed ? "retried" : "budget_exhausted");
            return ValueTask.FromResult(allowed);
        };
}
