using Microsoft.AspNetCore.Http;
using ShopEasy.Shared.Messaging;

namespace ShopEasy.OrderService.Services;

// Propagates the correlation id to outgoing HTTP requests.
// This links logs across OrderService → InventoryService / ProductService calls.
public class CorrelationIdHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        // The current id covers everything the middleware or a background worker has set up —
        // including an id this service generated itself because the caller sent none.
        // The incoming header remains the fallback for code running outside such a scope.
        if (CorrelationContext.Current is not null)
        {
            CorrelationContext.Propagate(request);
        }
        else
        {
            var correlationId = accessor.HttpContext?
                .Request.Headers[CorrelationContext.HeaderName].FirstOrDefault();

            if (!string.IsNullOrEmpty(correlationId))
                request.Headers.TryAddWithoutValidation(CorrelationContext.HeaderName, correlationId);
        }

        return await base.SendAsync(request, ct);
    }
}