using System.Net;
using System.Net.Sockets;
using Prometheus;
using ShopEasy.Shared.Resilience;

namespace ShopEasy.Gateway.Resilience;

// Retries a GET (or HEAD) once the connection to the service failed: the connection was refused or
// reset, the name did not resolve, or the service closed the connection without answering. In every
// such case the request either never arrived or was not answered, and repeating a request that only
// reads is harmless.
//
// What it deliberately does NOT do:
//  * retry anything else than GET and HEAD, or a request with a body: the service may already have
//    acted on it (a second POST /api/orders would place a second order);
//  * retry an answer: a 500 or 503 is the service's decision and is passed on to the caller as it is,
//    so a service that is struggling is not hit twice by every request;
//  * retry without limit: the retries are bounded per request and by the retry budget shared by all
//    requests, so an outage cannot multiply the load.
public sealed class SafeRetryHandler(
    HttpMessageHandler inner,
    RetryBudget budget,
    int maxRetries,
    Func<int, TimeSpan>? delay = null) : DelegatingHandler(inner)
{
    private static readonly Counter Retries = Metrics.CreateCounter(
        "shopeasy_gateway_retries_total",
        "Retries of proxied GET requests after a connection failure, and retries refused because the retry budget was used up",
        new CounterConfiguration { LabelNames = ["outcome"] }); // "retried", "budget_exhausted"

    private readonly Func<int, TimeSpan> _delay = delay ?? DefaultDelay;

    // 100 ms, then 200 ms, with up to half of it added or removed at random
    private static TimeSpan DefaultDelay(int attempt) =>
        TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt) * (0.5 + Random.Shared.NextDouble()));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (maxRetries <= 0 || !CanBeRepeated(request))
            return await base.SendAsync(request, cancellationToken);

        budget.RecordRequest();

        for (var attempt = 0; ; attempt++)
        {
            // a request message can only be sent once, so a retry sends a copy
            var message = attempt == 0 ? request : Copy(request);
            try
            {
                return await base.SendAsync(message, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < maxRetries
                                                  && IsConnectionFailure(ex)
                                                  && !cancellationToken.IsCancellationRequested)
            {
                if (!budget.TryAcquireRetry())
                {
                    Retries.WithLabels("budget_exhausted").Inc();
                    throw;
                }

                Retries.WithLabels("retried").Inc();
                await Task.Delay(_delay(attempt), cancellationToken);
            }
        }
    }

    public static bool CanBeRepeated(HttpRequestMessage request) =>
        (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head) && request.Content is null;

    // The request did not reach the service, or no answer came back
    public static bool IsConnectionFailure(HttpRequestException ex) =>
        ex.HttpRequestError is HttpRequestError.ConnectionError
            or HttpRequestError.NameResolutionError
            or HttpRequestError.ResponseEnded
        || (ex.HttpRequestError == HttpRequestError.Unknown
            && ex.InnerException is SocketException or IOException);

    private static HttpRequestMessage Copy(HttpRequestMessage original)
    {
        var copy = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy
        };

        foreach (var header in original.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in (IDictionary<string, object?>)original.Options)
            ((IDictionary<string, object?>)copy.Options)[option.Key] = option.Value;

        return copy;
    }
}
